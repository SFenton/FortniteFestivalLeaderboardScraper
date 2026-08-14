using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FSTService.Scraping.Replay;

internal sealed record TierZeroFileSnapshot(
    long Length,
    string Identity);

[ExcludeFromCodeCoverage(
    Justification = "Platform syscall shim; behavior is validated through package contract tests on supported runners.")]
internal static class TierZeroRegularFile
{
    private const int AtCurrentWorkingDirectory = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxBasicStats = 0x07ff;
    private const int StatBufferBytes = 256;
    private const int LinuxStatxModeOffset = 28;
    private const int LinuxStatxInodeOffset = 32;
    private const int LinuxStatxSizeOffset = 40;
    private const int LinuxStatxChangeTimeOffset = 96;
    private const int LinuxStatxModifiedTimeOffset = 112;
    private const int LinuxStatxDeviceMajorOffset = 136;
    private const int LinuxStatxDeviceMinorOffset = 140;
    private const ushort FileTypeMask = 0xf000;
    private const ushort RegularFileType = 0x8000;
    private const ushort SymbolicLinkType = 0xa000;
    private const int OpenReadOnly = 0;
    private const int OpenWriteOnly = 1;
    private const int OpenReadWrite = 2;
    private const int LinuxOpenCreate = 0x40;
    private const int BsdOpenCreate = 0x200;
    private const int LinuxOpenExclusive = 0x80;
    private const int BsdOpenExclusive = 0x800;
    private const int LinuxOpenNonBlock = 0x800;
    private const int LinuxOpenNoFollow = 0x20000;
    private const int LinuxOpenCloseOnExec = 0x80000;
    private const int LinuxOpenDirectory = 0x10000;
    private const int LinuxOpenPath = 0x200000;
    private const int DarwinOpenNonBlock = 0x4;
    private const int DarwinOpenNoFollow = 0x100;
    private const int DarwinOpenCloseOnExec = 0x01000000;
    private const int FreeBsdOpenCloseOnExec = 0x00100000;
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const uint OwnerReadWriteMode = 0x180;
    private static nint OpenAt2SystemCallNumber => 437;
    private const ulong ResolveNoSymbolicLinks = 0x04;
    private const ulong ResolveBeneath = 0x08;
    private const uint RenameNoReplace = 1;
    private const uint OwnerDirectoryMode = 0x1c0;
    private const int RemoveDirectory = 0x200;

    internal static long GetLength(string path) =>
        Inspect(path).Length;

    internal static TierZeroFileSnapshot Inspect(string path)
    {
        if (OperatingSystem.IsLinux())
            return InspectLinuxPath(path);
        if (OperatingSystem.IsMacOS())
            return InspectBsdPath(path, modeOffset: 4, inodeOffset: 8,
                sizeOffset: 96, changeTimeOffset: 64);
        if (OperatingSystem.IsFreeBSD())
            return InspectBsdPath(path, modeOffset: 24, inodeOffset: 8,
                sizeOffset: 112, changeTimeOffset: 80);

        var attributes = File.GetAttributes(path);
        if ((attributes &
             (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw InvalidRegularFile(path, attributes);
        }
        var file = new FileInfo(path);
        return new TierZeroFileSnapshot(
            file.Length,
            $"{file.Length}:{file.CreationTimeUtc.Ticks}:{file.LastWriteTimeUtc.Ticks}");
    }

    internal static async Task<byte[]> ReadAllBytesAsync(
        string path,
        TierZeroFileSnapshot? expected,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var opened = OpenRead(path, expected);
        if (opened.Snapshot.Length < 0 ||
            opened.Snapshot.Length > maximumBytes ||
            opened.Snapshot.Length > int.MaxValue)
        {
            throw new IOException(
                $"Tier-0 package entry size is invalid: {path}");
        }

        var bytes = new byte[(int)opened.Snapshot.Length];
        await opened.Stream.ReadExactlyAsync(
            bytes,
            cancellationToken);
        if (await opened.Stream.ReadAsync(
                new byte[1],
                cancellationToken) != 0)
        {
            throw new IOException(
                $"Tier-0 package entry grew while being read: {path}");
        }
        opened.ValidateUnchanged();
        return bytes;
    }

    internal static async Task<string> HashAsync(
        string path,
        TierZeroFileSnapshot? expected,
        CancellationToken cancellationToken)
    {
        await using var opened = OpenRead(path, expected);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(
            opened.Stream,
            cancellationToken);
        opened.ValidateUnchanged();
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static FileStream OpenExclusiveLock(
        string path,
        bool createIfMissing)
    {
        if (TryGetLinkTarget(path) is not null)
        {
            throw new TierZeroPackageException(
                TierZeroPackageError.SymbolicLinkDetected,
                $"Tier-0 package lock cannot be a symbolic link: {path}");
        }
        if (OperatingSystem.IsLinux() ||
            OperatingSystem.IsMacOS() ||
            OperatingSystem.IsFreeBSD())
        {
            var descriptor = OpenResolved(
                path,
                OpenReadWrite |
                (createIfMissing ? UnixOpenCreate : 0) |
                UnixOpenNonBlock |
                UnixOpenNoFollow |
                UnixOpenCloseOnExec,
                createIfMissing
                    ? OwnerReadWriteMode
                    : 0);
            if (descriptor < 0)
                throw OpenException(path);

            var handle = new SafeFileHandle(
                new IntPtr(descriptor),
                ownsHandle: true);
            try
            {
                _ = InspectDescriptor(descriptor, path);
                if (Flock(
                        descriptor,
                        LockExclusive | LockNonBlocking) != 0)
                {
                    throw new IOException(
                        $"Tier-0 package lock is already held: {path}",
                        new Win32Exception(
                            Marshal.GetLastPInvokeError()));
                }

                return new FileStream(
                    handle,
                    FileAccess.ReadWrite,
                    bufferSize: 1,
                    isAsync: false);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        if (File.Exists(path))
            _ = Inspect(path);
        return new FileStream(
            path,
            createIfMissing
                ? FileMode.OpenOrCreate
                : FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.Asynchronous |
            FileOptions.WriteThrough);
    }

    internal static FileStream CreateNewWrite(
        string path,
        int bufferSize)
    {
        if (TryGetLinkTarget(path) is not null)
        {
            throw new TierZeroPackageException(
                TierZeroPackageError.SymbolicLinkDetected,
                $"Tier-0 output path cannot be a symbolic link: {path}");
        }
        if (OperatingSystem.IsLinux() ||
            OperatingSystem.IsMacOS() ||
            OperatingSystem.IsFreeBSD())
        {
            var descriptor = OpenResolved(
                path,
                OpenWriteOnly |
                UnixOpenCreate |
                UnixOpenExclusive |
                UnixOpenNoFollow |
                UnixOpenCloseOnExec,
                OwnerReadWriteMode);
            if (descriptor < 0)
                throw OpenException(path);
            var handle = new SafeFileHandle(
                new IntPtr(descriptor),
                ownsHandle: true);
            try
            {
                _ = InspectDescriptor(descriptor, path);
                return new FileStream(
                    handle,
                    FileAccess.Write,
                    bufferSize,
                    isAsync: false);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        return new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize,
            FileOptions.Asynchronous |
            FileOptions.WriteThrough);
    }

    internal static void Move(
        string source,
        string destination,
        bool overwrite)
    {
        if (!OperatingSystem.IsLinux())
        {
            File.Move(source, destination, overwrite);
            return;
        }

        var sourceParent = Path.GetDirectoryName(source)
            ?? throw new IOException(
                $"Tier-0 source path has no parent: {source}");
        var destinationParent = Path.GetDirectoryName(destination)
            ?? throw new IOException(
                $"Tier-0 destination path has no parent: {destination}");
        if (!string.Equals(
                sourceParent,
                destinationParent,
                StringComparison.Ordinal))
        {
            throw new IOException(
                "Tier-0 atomic moves must remain in one directory.");
        }

        var descriptor = OpenResolved(
            sourceParent,
            LinuxOpenPath |
            LinuxOpenDirectory |
            LinuxOpenNoFollow |
            LinuxOpenCloseOnExec,
            0);
        if (descriptor < 0)
            throw OpenException(sourceParent);
        using var handle = new SafeFileHandle(
            new IntPtr(descriptor),
            ownsHandle: true);
        if (RenameAt2Resolved(
                descriptor,
                Path.GetFileName(source),
                descriptor,
                Path.GetFileName(destination),
                overwrite ? 0 : RenameNoReplace) != 0)
        {
            throw new IOException(
                $"Could not atomically move Tier-0 package entry '{source}'.",
                new Win32Exception(
                    Marshal.GetLastPInvokeError()));
        }
    }

    internal static void CreateDirectoryUnderRoot(
        string root,
        string relativeDirectory)
    {
        if (relativeDirectory.Length == 0)
            return;
        if (!OperatingSystem.IsLinux())
        {
            var path = TierZeroPackagePath.ResolveUnderRoot(
                root,
                relativeDirectory);
            Directory.CreateDirectory(path);
            TierZeroPackagePath.EnsureNoSymbolicLinks(
                root,
                path,
                includeCandidate: true);
            return;
        }

        var rootDescriptor = OpenResolved(
            root,
            LinuxOpenPath |
            LinuxOpenDirectory |
            LinuxOpenNoFollow |
            LinuxOpenCloseOnExec,
            0);
        if (rootDescriptor < 0)
            throw OpenException(root);
        var handles = new List<SafeFileHandle>
        {
            new(
                new IntPtr(rootDescriptor),
                ownsHandle: true),
        };
        try
        {
            var currentDescriptor = rootDescriptor;
            var currentPath = root;
            foreach (var segment in relativeDirectory.Split('/'))
            {
                currentPath = Path.Combine(currentPath, segment);
                var descriptor = OpenRelativeDirectory(
                    currentDescriptor,
                    segment);
                if (descriptor < 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error != 2)
                        throw OpenException(currentPath, error);
                    if (MkdirAt(
                            currentDescriptor,
                            segment,
                            OwnerDirectoryMode) != 0)
                    {
                        error = Marshal.GetLastPInvokeError();
                        if (error != 17)
                            throw OpenException(currentPath, error);
                    }
                    descriptor = OpenRelativeDirectory(
                        currentDescriptor,
                        segment);
                    if (descriptor < 0)
                    {
                        throw OpenException(
                            currentPath,
                            Marshal.GetLastPInvokeError());
                    }
                }

                handles.Add(new SafeFileHandle(
                    new IntPtr(descriptor),
                    ownsHandle: true));
                currentDescriptor = descriptor;
            }
        }
        finally
        {
            foreach (var handle in handles.AsEnumerable().Reverse())
                handle.Dispose();
        }
    }

    internal static void DeleteFile(string path) =>
        DeleteEntry(path, removeDirectory: false);

    internal static void DeleteDirectory(string path) =>
        DeleteEntry(path, removeDirectory: true);

    private static OpenedRegularFile OpenRead(
        string path,
        TierZeroFileSnapshot? expected)
    {
        if (OperatingSystem.IsLinux() ||
            OperatingSystem.IsMacOS() ||
            OperatingSystem.IsFreeBSD())
        {
            var descriptor = OpenResolved(
                path,
                OpenReadOnly |
                UnixOpenNonBlock |
                UnixOpenNoFollow |
                UnixOpenCloseOnExec,
                0);
            if (descriptor < 0)
                throw OpenException(path);

            var handle = new SafeFileHandle(
                new IntPtr(descriptor),
                ownsHandle: true);
            try
            {
                var snapshot = InspectDescriptor(descriptor, path);
                EnsureExpected(path, expected, snapshot);
                return new OpenedRegularFile(
                    path,
                    handle,
                    new FileStream(
                        handle,
                        FileAccess.Read,
                        bufferSize: 64 * 1024,
                        isAsync: false),
                    snapshot);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        var before = Inspect(path);
        EnsureExpected(path, expected, before);
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        var after = Inspect(path);
        if (before != after)
        {
            stream.Dispose();
            throw Changed(path);
        }
        return new OpenedRegularFile(
            path,
            stream.SafeFileHandle,
            stream,
            before);
    }

    private static TierZeroFileSnapshot InspectDescriptor(
        int descriptor,
        string path)
    {
        if (OperatingSystem.IsLinux())
            return InspectLinux(descriptor, "", AtEmptyPath, path);

        var buffer = Marshal.AllocHGlobal(StatBufferBytes);
        try
        {
            Zero(buffer);
            if (FStatForCurrentPlatform(
                    descriptor,
                    buffer) != 0)
                throw InspectException(path);
            return OperatingSystem.IsMacOS()
                ? ParseBsd(
                    buffer,
                    path,
                    modeOffset: 4,
                    inodeOffset: 8,
                    sizeOffset: 96,
                    changeTimeOffset: 64)
                : ParseBsd(
                    buffer,
                    path,
                    modeOffset: 24,
                    inodeOffset: 8,
                    sizeOffset: 112,
                    changeTimeOffset: 80);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static TierZeroFileSnapshot InspectLinuxPath(string path)
    {
        var descriptor = OpenResolved(
            path,
            LinuxOpenPath |
            LinuxOpenNoFollow |
            LinuxOpenCloseOnExec,
            0);
        if (descriptor < 0)
            throw OpenException(path);
        using var handle = new SafeFileHandle(
            new IntPtr(descriptor),
            ownsHandle: true);
        return InspectDescriptor(descriptor, path);
    }

    private static TierZeroFileSnapshot InspectLinux(
        int directoryFileDescriptor,
        string statPath,
        int flags,
        string displayPath)
    {
        var buffer = Marshal.AllocHGlobal(StatBufferBytes);
        try
        {
            Zero(buffer);
            if (Statx(
                    directoryFileDescriptor,
                    statPath,
                    flags,
                    StatxBasicStats,
                    buffer) != 0)
            {
                throw InspectException(displayPath);
            }

            var mode = unchecked((ushort)Marshal.ReadInt16(
                buffer,
                LinuxStatxModeOffset));
            EnsureRegularType(displayPath, mode);
            var length = Marshal.ReadInt64(
                buffer,
                LinuxStatxSizeOffset);
            var inode = unchecked((ulong)Marshal.ReadInt64(
                buffer,
                LinuxStatxInodeOffset));
            var changeSeconds = Marshal.ReadInt64(
                buffer,
                LinuxStatxChangeTimeOffset);
            var changeNanos = unchecked((uint)Marshal.ReadInt32(
                buffer,
                LinuxStatxChangeTimeOffset + 8));
            var modifiedSeconds = Marshal.ReadInt64(
                buffer,
                LinuxStatxModifiedTimeOffset);
            var modifiedNanos = unchecked((uint)Marshal.ReadInt32(
                buffer,
                LinuxStatxModifiedTimeOffset + 8));
            var deviceMajor = unchecked((uint)Marshal.ReadInt32(
                buffer,
                LinuxStatxDeviceMajorOffset));
            var deviceMinor = unchecked((uint)Marshal.ReadInt32(
                buffer,
                LinuxStatxDeviceMinorOffset));
            return new TierZeroFileSnapshot(
                length,
                $"{deviceMajor}:{deviceMinor}:{inode}:{length}:{changeSeconds}:{changeNanos}:{modifiedSeconds}:{modifiedNanos}");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static TierZeroFileSnapshot InspectBsdPath(
        string path,
        int modeOffset,
        int inodeOffset,
        int sizeOffset,
        int changeTimeOffset)
    {
        var buffer = Marshal.AllocHGlobal(StatBufferBytes);
        try
        {
            Zero(buffer);
            if (LStatForCurrentPlatform(
                    path,
                    buffer) != 0)
                throw InspectException(path);
            return ParseBsd(
                buffer,
                path,
                modeOffset,
                inodeOffset,
                sizeOffset,
                changeTimeOffset);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static TierZeroFileSnapshot ParseBsd(
        IntPtr buffer,
        string path,
        int modeOffset,
        int inodeOffset,
        int sizeOffset,
        int changeTimeOffset)
    {
        var mode = unchecked((ushort)Marshal.ReadInt16(
            buffer,
            modeOffset));
        EnsureRegularType(path, mode);
        var inode = unchecked((ulong)Marshal.ReadInt64(
            buffer,
            inodeOffset));
        var length = Marshal.ReadInt64(buffer, sizeOffset);
        var changeSeconds = Marshal.ReadInt64(
            buffer,
            changeTimeOffset);
        var changeNanos = unchecked((ulong)Marshal.ReadInt64(
            buffer,
            changeTimeOffset + 8));
        return new TierZeroFileSnapshot(
            length,
            $"{inode}:{length}:{changeSeconds}:{changeNanos}");
    }

    private static void EnsureExpected(
        string path,
        TierZeroFileSnapshot? expected,
        TierZeroFileSnapshot actual)
    {
        if (expected is not null && expected != actual)
            throw Changed(path);
    }

    private static void EnsureRegularType(
        string path,
        ushort mode)
    {
        var type = (ushort)(mode & FileTypeMask);
        if (type == RegularFileType)
            return;
        if (type == SymbolicLinkType)
        {
            throw new TierZeroPackageException(
                TierZeroPackageError.SymbolicLinkDetected,
                $"Tier-0 package entry is a symbolic link: {path}");
        }
        throw InvalidRegularFile(path, FileAttributes.Normal);
    }

    private static TierZeroPackageException InvalidRegularFile(
        string path,
        FileAttributes attributes) =>
        new(
            (attributes & FileAttributes.ReparsePoint) != 0
                ? TierZeroPackageError.SymbolicLinkDetected
                : TierZeroPackageError.InvalidPath,
            $"Tier-0 package entry is not a regular file: {path}");

    private static IOException Changed(string path) =>
        new($"Tier-0 package entry changed while being read: {path}");

    private static Exception OpenException(string path) =>
        OpenException(path, Marshal.GetLastPInvokeError());

    private static Exception OpenException(
        string path,
        int error)
    {
        if ((OperatingSystem.IsLinux() && error == 40) ||
            ((OperatingSystem.IsMacOS() ||
              OperatingSystem.IsFreeBSD()) && error == 62))
        {
            return new TierZeroPackageException(
                TierZeroPackageError.SymbolicLinkDetected,
                $"Tier-0 package path contains a symbolic link: {path}");
        }
        try
        {
            if (TryGetLinkTarget(path) is not null)
            {
                return new TierZeroPackageException(
                    TierZeroPackageError.SymbolicLinkDetected,
                    $"Tier-0 package entry is a symbolic link: {path}");
            }
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException)
        {
        }
        return new IOException(
            $"Could not open Tier-0 package entry '{path}'.",
            new Win32Exception(error));
    }

    private static string? TryGetLinkTarget(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IOException InspectException(string path) =>
        new(
            $"Could not inspect Tier-0 package entry '{path}'.",
            new Win32Exception(Marshal.GetLastPInvokeError()));

    private static int UnixOpenNonBlock =>
        OperatingSystem.IsLinux()
            ? LinuxOpenNonBlock
            : DarwinOpenNonBlock;

    private static int UnixOpenCreate =>
        OperatingSystem.IsLinux()
            ? LinuxOpenCreate
            : BsdOpenCreate;

    private static int UnixOpenExclusive =>
        OperatingSystem.IsLinux()
            ? LinuxOpenExclusive
            : BsdOpenExclusive;

    private static int UnixOpenNoFollow =>
        OperatingSystem.IsLinux()
            ? LinuxOpenNoFollow
            : DarwinOpenNoFollow;

    private static int UnixOpenCloseOnExec =>
        OperatingSystem.IsLinux()
            ? LinuxOpenCloseOnExec
            : OperatingSystem.IsMacOS()
                ? DarwinOpenCloseOnExec
                : FreeBsdOpenCloseOnExec;

    private static void Zero(IntPtr buffer)
    {
        for (var offset = 0; offset < StatBufferBytes; offset += 8)
            Marshal.WriteInt64(buffer, offset, 0);
    }

    private static int LStatForCurrentPlatform(
        string path,
        IntPtr buffer) =>
        OperatingSystem.IsMacOS() &&
        RuntimeInformation.ProcessArchitecture ==
        Architecture.X64
            ? LStatInode64(path, buffer)
            : LStat(path, buffer);

    private static int FStatForCurrentPlatform(
        int descriptor,
        IntPtr buffer) =>
        OperatingSystem.IsMacOS() &&
        RuntimeInformation.ProcessArchitecture ==
        Architecture.X64
            ? FStatInode64(descriptor, buffer)
            : FStat(descriptor, buffer);

    private static int OpenResolved(
        string path,
        int flags,
        uint mode)
    {
        if (!OperatingSystem.IsLinux())
            return Open(path, flags, mode);
        var how = new OpenHow
        {
            Flags = unchecked((ulong)flags),
            Mode = mode,
            Resolve = ResolveNoSymbolicLinks,
        };
        return unchecked((int)OpenAt2(
            OpenAt2SystemCallNumber,
            AtCurrentWorkingDirectory,
            path,
            ref how,
            (nuint)Marshal.SizeOf<OpenHow>()));
    }

    private static int OpenRelativeDirectory(
        int parentDescriptor,
        string segment)
    {
        var how = new OpenHow
        {
            Flags = unchecked((ulong)(
                LinuxOpenPath |
                LinuxOpenDirectory |
                LinuxOpenNoFollow |
                LinuxOpenCloseOnExec)),
            Mode = 0,
            Resolve =
                ResolveNoSymbolicLinks |
                ResolveBeneath,
        };
        return unchecked((int)OpenAt2(
            OpenAt2SystemCallNumber,
            parentDescriptor,
            segment,
            ref how,
            (nuint)Marshal.SizeOf<OpenHow>()));
    }

    private static int RenameAt2Resolved(
        int oldDirectoryFileDescriptor,
        string oldPath,
        int newDirectoryFileDescriptor,
        string newPath,
        uint flags) =>
        unchecked((int)RenameAt2SystemCall(
            RenameAt2SystemCallNumber,
            oldDirectoryFileDescriptor,
            oldPath,
            newDirectoryFileDescriptor,
            newPath,
            flags));

    private static nint RenameAt2SystemCallNumber =>
        RuntimeInformation.ProcessArchitecture.ToString() switch
        {
            "X64" => 316,
            "X86" => 353,
            "Arm64" => 276,
            "Arm" => 382,
            "S390x" => 347,
            "Ppc64le" => 357,
            "RiscV64" => 276,
            "LoongArch64" => 276,
            _ => throw new PlatformNotSupportedException(
                $"renameat2 is not mapped for {RuntimeInformation.ProcessArchitecture}."),
        };

    private static void DeleteEntry(
        string path,
        bool removeDirectory)
    {
        if (!OperatingSystem.IsLinux())
        {
            if (removeDirectory)
                Directory.Delete(path);
            else
                File.Delete(path);
            return;
        }

        var parent = Path.GetDirectoryName(path)
            ?? throw new IOException(
                $"Tier-0 delete path has no parent: {path}");
        var descriptor = OpenResolved(
            parent,
            LinuxOpenPath |
            LinuxOpenDirectory |
            LinuxOpenNoFollow |
            LinuxOpenCloseOnExec,
            0);
        if (descriptor < 0)
            throw OpenException(parent);
        using var handle = new SafeFileHandle(
            new IntPtr(descriptor),
            ownsHandle: true);
        if (UnlinkAt(
                descriptor,
                Path.GetFileName(path),
                removeDirectory ? RemoveDirectory : 0) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != 2)
            {
                throw new IOException(
                    $"Could not delete Tier-0 package entry '{path}'.",
                    new Win32Exception(error));
            }
        }
    }

    private sealed class OpenedRegularFile : IAsyncDisposable
    {
        private readonly string _path;
        private readonly SafeFileHandle _handle;

        internal OpenedRegularFile(
            string path,
            SafeFileHandle handle,
            FileStream stream,
            TierZeroFileSnapshot snapshot)
        {
            _path = path;
            _handle = handle;
            Stream = stream;
            Snapshot = snapshot;
        }

        internal FileStream Stream { get; }
        internal TierZeroFileSnapshot Snapshot { get; }

        internal void ValidateUnchanged()
        {
            TierZeroFileSnapshot current;
            if (OperatingSystem.IsLinux() ||
                OperatingSystem.IsMacOS() ||
                OperatingSystem.IsFreeBSD())
            {
                current = InspectDescriptor(
                    _handle.DangerousGetHandle().ToInt32(),
                    _path);
            }
            else
            {
                current = Inspect(_path);
            }
            if (current != Snapshot)
                throw Changed(_path);
        }

        public ValueTask DisposeAsync() =>
            Stream.DisposeAsync();
    }

    [DllImport(
        "libc",
        EntryPoint = "statx",
        SetLastError = true,
        CharSet = CharSet.Ansi,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    private static extern int Statx(
        int directoryFileDescriptor,
        string path,
        int flags,
        uint mask,
        IntPtr buffer);

    [DllImport(
        "libc",
        EntryPoint = "lstat",
        SetLastError = true,
        CharSet = CharSet.Ansi,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    private static extern int LStat(
        string path,
        IntPtr buffer);

    [DllImport(
        "libc",
        EntryPoint = "lstat$INODE64",
        SetLastError = true,
        CharSet = CharSet.Ansi,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    private static extern int LStatInode64(
        string path,
        IntPtr buffer);

    [DllImport(
        "libc",
        EntryPoint = "fstat",
        SetLastError = true)]
    private static extern int FStat(
        int descriptor,
        IntPtr buffer);

    [DllImport(
        "libc",
        EntryPoint = "fstat$INODE64",
        SetLastError = true)]
    private static extern int FStatInode64(
        int descriptor,
        IntPtr buffer);

    [DllImport(
        "libc",
        EntryPoint = "open",
        SetLastError = true,
        CharSet = CharSet.Ansi,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    private static extern int Open(
        string path,
        int flags,
        uint mode);

    [DllImport(
        "libc",
        EntryPoint = "flock",
        SetLastError = true)]
    private static extern int Flock(
        int descriptor,
        int operation);

    [DllImport(
        "libc",
        EntryPoint = "mkdirat",
        SetLastError = true,
        CharSet = CharSet.Ansi,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    private static extern int MkdirAt(
        int directoryFileDescriptor,
        string path,
        uint mode);

    [DllImport(
        "libc",
        EntryPoint = "unlinkat",
        SetLastError = true,
        CharSet = CharSet.Ansi,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    private static extern int UnlinkAt(
        int directoryFileDescriptor,
        string path,
        int flags);

    [DllImport(
        "libc",
        EntryPoint = "syscall",
        SetLastError = true,
        CharSet = CharSet.Ansi,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    private static extern nint RenameAt2SystemCall(
        nint systemCall,
        int oldDirectoryFileDescriptor,
        string oldPath,
        int newDirectoryFileDescriptor,
        string newPath,
        uint flags);

    [DllImport(
        "libc",
        EntryPoint = "syscall",
        SetLastError = true,
        CharSet = CharSet.Ansi,
        BestFitMapping = false,
        ThrowOnUnmappableChar = true)]
    private static extern nint OpenAt2(
        nint systemCall,
        int directoryFileDescriptor,
        string path,
        ref OpenHow how,
        nuint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenHow
    {
        internal ulong Flags;
        internal ulong Mode;
        internal ulong Resolve;
    }
}
