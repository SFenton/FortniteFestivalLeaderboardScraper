using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

sealed class RequestSpec { public string Url { get; set; } = ""; public string Instrument { get; set; } = ""; public string ScopeHash { get; set; } = ""; }
sealed class Stage { public string Name { get; set; } = ""; public string RoutingMode { get; set; } = "fixed-round-robin"; public int PerExitRps { get; set; } public int PerExitConcurrency { get; set; } public int GlobalRps { get; set; } public int RequestCount { get; set; } public int TimeoutSeconds { get; set; } public int CooldownSeconds { get; set; } }
sealed class Config { public string AccessToken { get; set; } = ""; public string UserAgent { get; set; } = ""; public List<string> Proxies { get; set; } = []; public List<RequestSpec> Workload { get; set; } = []; public List<Stage> Stages { get; set; } = []; public string OutputDir { get; set; } = ""; public string ScratchDir { get; set; } = ""; public int FullScrapePages { get; set; } public double PriorNetworkSeconds { get; set; } public List<string> HealthUrls { get; set; } = []; }
sealed class HealthResult { public string Url { get; set; } = ""; public int Status { get; set; } public double Seconds { get; set; } public string? Error { get; set; } }
sealed class Result { public int Index { get; set; } public string Proxy { get; set; } = ""; public string Instrument { get; set; } = ""; public string ScopeHash { get; set; } = ""; public string Category { get; set; } = ""; public int HttpStatus { get; set; } public int CurlExit { get; set; } = -1; public string StartedAtUtc { get; set; } = ""; public double ElapsedSeconds { get; set; } public double ConnectSeconds { get; set; } public double AppConnectSeconds { get; set; } public double StartTransferSeconds { get; set; } public int ConnectionCount { get; set; } public long BodyBytes { get; set; } public int EntryCount { get; set; } public string? EntriesSha256 { get; set; } public string? HttpVersion { get; set; } }
sealed class MiniAgg { public int Requests { get; set; } public int Valid { get; set; } public double ValidPercent { get; set; } public int Blocks { get; set; } public int Timeouts { get; set; } public int Http429 { get; set; } public int Http503 { get; set; } public int Other5xx { get; set; } public double P95Seconds { get; set; } }
sealed class Aggregate { public int Requests { get; set; } public int Valid { get; set; } public double ValidPercent { get; set; } public Dictionary<string,int> CategoryCounts { get; set; } = []; public double UsefulPagesPerSecond { get; set; } public double UsefulRowsPerSecond { get; set; } public double WireSendsPerUsefulRequest { get; set; } public double BytesPerWireSend { get; set; } public double BytesPerUsefulRequest { get; set; } public double LatencyP50Seconds { get; set; } public double LatencyP95Seconds { get; set; } public double LatencyP99Seconds { get; set; } public double ConnectP50Seconds { get; set; } public double ConnectP95Seconds { get; set; } public double AppConnectP50Seconds { get; set; } public double AppConnectP95Seconds { get; set; } public double StartTransferP50Seconds { get; set; } public double StartTransferP95Seconds { get; set; } public int ConnectionCount { get; set; } public double ProjectedNetworkSeconds { get; set; } public double ProjectedNetworkDeltaPercentVsBaseline { get; set; } public Dictionary<string,int> ScopeFingerprintVariants { get; set; } = []; public int MultiVariantScopeCount { get; set; } public Dictionary<string,MiniAgg> PerProxy { get; set; } = []; public Dictionary<string,MiniAgg> PerInstrument { get; set; } = []; }
sealed class ResourceMetrics { public long PeakMemoryBytes { get; set; } public long PeakPids { get; set; } public long PeakActiveCurlProcesses { get; set; } public long PeakScratchBytes { get; set; } public long PeakScratchFiles { get; set; } public double CgroupCpuSeconds { get; set; } public long ScratchBytesAfter { get; set; } public long ScratchFilesAfter { get; set; } }
sealed class StageReport { public Stage Stage { get; set; } = new(); public string StartedAtUtc { get; set; } = ""; public string FinishedAtUtc { get; set; } = ""; public double WallSeconds { get; set; } public int EffectiveExits { get; set; } public int WorkloadScopes { get; set; } public List<string> DistinctInstruments { get; set; } = []; public List<HealthResult> HealthBefore { get; set; } = []; public List<HealthResult> HealthAfter { get; set; } = []; public Aggregate Aggregate { get; set; } = new(); public ResourceMetrics Resources { get; set; } = new(); public List<Result> Results { get; set; } = []; public bool GatePassed { get; set; } public List<string> GateReasons { get; set; } = []; }

sealed class RateGate(int rps)
{
    readonly object gate = new();
    readonly TimeSpan interval = rps > 0 ? TimeSpan.FromSeconds(1d / rps) : TimeSpan.Zero;
    DateTimeOffset next;
    public async Task WaitAsync(CancellationToken ct)
    {
        if (interval == TimeSpan.Zero) return;
        DateTimeOffset slot;
        lock (gate)
        {
            var now = DateTimeOffset.UtcNow;
            slot = next > now ? next : now;
            next = slot + interval;
        }
        var delay = slot - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
    }
}

sealed class Sampler : IAsyncDisposable
{
    readonly string scratchDir;
    readonly CancellationTokenSource cts = new();
    readonly Task loop;
    readonly long cpuStart;
    long peakMemory, peakPids, peakScratchBytes, peakScratchFiles;
    public Sampler(string dir)
    {
        scratchDir = dir;
        cpuStart = ReadCpuUsec();
        loop = RunAsync();
    }
    static long ReadLong(string path) => long.TryParse(File.Exists(path) ? File.ReadAllText(path).Trim() : "0", out var n) ? n : 0;
    public static long ReadCpuUsec()
    {
        if (!File.Exists("/sys/fs/cgroup/cpu.stat")) return 0;
        foreach (var line in File.ReadLines("/sys/fs/cgroup/cpu.stat"))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 2 && fields[0] == "usage_usec" && long.TryParse(fields[1], out var n)) return n;
        }
        return 0;
    }
    static (long Bytes,long Files) Scan(string path)
    {
        if (!Directory.Exists(path)) return (0,0);
        long bytes=0, files=0;
        try { foreach (var file in Directory.EnumerateFiles(path,"*",SearchOption.AllDirectories)) { try { bytes += new FileInfo(file).Length; files++; } catch { } } } catch { }
        return (bytes,files);
    }
    static void Max(ref long target, long value) { while (true) { var old=Interlocked.Read(ref target); if (value<=old || Interlocked.CompareExchange(ref target,value,old)==old) return; } }
    async Task RunAsync()
    {
        while (!cts.IsCancellationRequested)
        {
            Max(ref peakMemory, ReadLong("/sys/fs/cgroup/memory.current")); Max(ref peakPids, ReadLong("/sys/fs/cgroup/pids.current"));
            var sample=Scan(scratchDir); Max(ref peakScratchBytes,sample.Bytes); Max(ref peakScratchFiles,sample.Files);
            try { await Task.Delay(100,cts.Token); } catch (OperationCanceledException) { }
        }
    }
    public ResourceMetrics Finish(long peakCurl)
    {
        var sample=Scan(scratchDir);
        return new ResourceMetrics { PeakMemoryBytes=Interlocked.Read(ref peakMemory),PeakPids=Interlocked.Read(ref peakPids),PeakActiveCurlProcesses=peakCurl,PeakScratchBytes=Interlocked.Read(ref peakScratchBytes),PeakScratchFiles=Interlocked.Read(ref peakScratchFiles),CgroupCpuSeconds=(ReadCpuUsec()-cpuStart)/1_000_000d,ScratchBytesAfter=sample.Bytes,ScratchFilesAfter=sample.Files };
    }
    public async ValueTask DisposeAsync() { cts.Cancel(); await loop; cts.Dispose(); }
}

static class Runner
{
    static long activeCurl, peakActiveCurl;
    static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true,DefaultIgnoreCondition=JsonIgnoreCondition.WhenWritingNull };
    static void AtomicMax(ref long target,long value){while(true){var old=Interlocked.Read(ref target);if(value<=old||Interlocked.CompareExchange(ref target,value,old)==old)return;}}
    static string Escape(string v)=>v.Replace("\\","\\\\",StringComparison.Ordinal).Replace("\"","\\\"",StringComparison.Ordinal).Replace("\r","\\r",StringComparison.Ordinal).Replace("\n","\\n",StringComparison.Ordinal).Replace("\t","\\t",StringComparison.Ordinal);
    static void Opt(StringBuilder sb,string name,string? value=null){sb.Append(name);if(value is not null)sb.Append(" = \"").Append(Escape(value)).Append('"');sb.Append('\n');}
    static string Correlation(){Span<byte>b=stackalloc byte[16];RandomNumberGenerator.Fill(b);return "FN-"+Convert.ToBase64String(b).TrimEnd('=').Replace('+','-').Replace('/','_');}
    static async Task<Result> RunCurlAsync(Config cfg,Stage stage,RequestSpec spec,string proxy,int index,string scratch,CancellationToken parent)
    {
        var result=new Result{Index=index,Proxy=proxy,Instrument=spec.Instrument,ScopeHash=spec.ScopeHash,StartedAtUtc=DateTimeOffset.UtcNow.ToString("O")};
        var bodyPath=Path.Combine(scratch,$"response-{index:D6}.bin"); var sb=new StringBuilder();
        Opt(sb,"silent");Opt(sb,"show-error");Opt(sb,"http1.1");Opt(sb,"compressed");Opt(sb,"max-time",stage.TimeoutSeconds.ToString(CultureInfo.InvariantCulture));Opt(sb,"request","GET");Opt(sb,"url",spec.Url);Opt(sb,"output",bodyPath);Opt(sb,"write-out","\n__FST_META__%{http_code}|%{content_type}|%{time_total}|%{size_download}|%{time_connect}|%{http_version}|%{time_appconnect}|%{time_starttransfer}|%{num_connects}\n");Opt(sb,"proxy",$"http://{proxy}:8888");Opt(sb,"header","Authorization: bearer "+cfg.AccessToken);Opt(sb,"header","User-Agent: "+cfg.UserAgent);Opt(sb,"header","X-Epic-Correlation-ID: "+Correlation());Opt(sb,"header","Accept: */*");
        using var cts=CancellationTokenSource.CreateLinkedTokenSource(parent);cts.CancelAfter(TimeSpan.FromSeconds(stage.TimeoutSeconds+5));
        using var p=new Process{StartInfo=new ProcessStartInfo("curl"){UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true}};p.StartInfo.ArgumentList.Add("--config");p.StartInfo.ArgumentList.Add("-");
        try
        {
            p.Start();var cur=Interlocked.Increment(ref activeCurl);AtomicMax(ref peakActiveCurl,cur);
            await p.StandardInput.WriteAsync(sb.ToString());p.StandardInput.Close();
            var stdoutTask=p.StandardOutput.ReadToEndAsync(cts.Token);var stderrTask=p.StandardError.ReadToEndAsync(cts.Token);
            try{await p.WaitForExitAsync(cts.Token);}catch(OperationCanceledException){try{p.Kill(true);}catch{} throw;}
            var stdout=await stdoutTask;_ = await stderrTask;result.CurlExit=p.ExitCode;
            var pos=stdout.LastIndexOf("__FST_META__",StringComparison.Ordinal);
            if(pos>=0)
            {
                var parts=stdout[(pos+12)..].Trim().Split('|');
                if(parts.Length>0&&int.TryParse(parts[0],out var status))result.HttpStatus=status;
                if(parts.Length>2&&double.TryParse(parts[2],CultureInfo.InvariantCulture,out var elapsed))result.ElapsedSeconds=elapsed;
                if(parts.Length>3&&double.TryParse(parts[3],CultureInfo.InvariantCulture,out var bytes))result.BodyBytes=(long)bytes;
                if(parts.Length>4&&double.TryParse(parts[4],CultureInfo.InvariantCulture,out var connect))result.ConnectSeconds=connect;
                if(parts.Length>5)result.HttpVersion=parts[5];
                if(parts.Length>6&&double.TryParse(parts[6],CultureInfo.InvariantCulture,out var appConnect))result.AppConnectSeconds=appConnect;
                if(parts.Length>7&&double.TryParse(parts[7],CultureInfo.InvariantCulture,out var startTransfer))result.StartTransferSeconds=startTransfer;
                if(parts.Length>8&&int.TryParse(parts[8],CultureInfo.InvariantCulture,out var connections))result.ConnectionCount=connections;
            }
        }
        catch(OperationCanceledException){result.CurlExit=28;result.Category="timeout";}
        catch{result.Category="transport";}
        finally{Interlocked.Decrement(ref activeCurl);}
        byte[] body=[];try{body=await File.ReadAllBytesAsync(bodyPath,parent);}catch{}finally{try{File.Delete(bodyPath);}catch{}}
        if(result.BodyBytes==0)result.BodyBytes=body.LongLength;
        JsonDocument? doc=null;try{if(body.Length>0)doc=JsonDocument.Parse(body);}catch{}
        var jsonOk=doc is not null&&doc.RootElement.ValueKind==JsonValueKind.Object;
        JsonElement entries=default;
        var entriesOk=jsonOk&&doc!.RootElement.TryGetProperty("entries",out entries)&&entries.ValueKind==JsonValueKind.Array;
        var pageOk=jsonOk&&doc!.RootElement.TryGetProperty("page",out var page)&&page.ValueKind==JsonValueKind.Number;
        var totalOk=jsonOk&&doc!.RootElement.TryGetProperty("totalPages",out var total)&&total.ValueKind==JsonValueKind.Number;
        if(result.Category.Length==0)
        {
            if(result.CurlExit==0&&result.HttpStatus==200&&entriesOk&&pageOk&&totalOk){result.Category="valid_epic_json";result.EntryCount=entries.GetArrayLength();result.EntriesSha256=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entries.GetRawText()))).ToLowerInvariant();}
            else if(result.CurlExit==28)result.Category="timeout";else if(result.CurlExit!=0)result.Category="transport";else if(result.HttpStatus==429)result.Category="rate_limited_429";else if(result.HttpStatus==503)result.Category="http_503";else if(result.HttpStatus>=500)result.Category="other_5xx";else if(result.HttpStatus==403&&!jsonOk)result.Category="cdn_non_json_403";else if((result.HttpStatus==401||result.HttpStatus==403)&&jsonOk)result.Category="json_auth_entitlement";else if(result.HttpStatus==200&&!jsonOk)result.Category="malformed_or_non_json_200";else if(result.HttpStatus==200)result.Category="malformed_json_shape";else if(jsonOk)result.Category="json_http_error";else result.Category="non_json_http_error";
        }
        doc?.Dispose();return result;
    }
    static double Percentile(IEnumerable<double> values,double p){var a=values.Order().ToArray();if(a.Length==0)return 0;var i=(int)Math.Round(p*(a.Length-1),MidpointRounding.AwayFromZero);return a[Math.Clamp(i,0,a.Length-1)];}
    static MiniAgg Mini(IEnumerable<Result> source){var r=source.ToList();return new MiniAgg{Requests=r.Count,Valid=r.Count(x=>x.Category=="valid_epic_json"),ValidPercent=r.Count==0?0:100d*r.Count(x=>x.Category=="valid_epic_json")/r.Count,Blocks=r.Count(x=>x.Category=="cdn_non_json_403"),Timeouts=r.Count(x=>x.Category=="timeout"),Http429=r.Count(x=>x.Category=="rate_limited_429"),Http503=r.Count(x=>x.Category=="http_503"),Other5xx=r.Count(x=>x.Category=="other_5xx"),P95Seconds=Percentile(r.Select(x=>x.ElapsedSeconds),.95)};}
    static Aggregate AggregateResults(List<Result> r,double wall,int fullPages,double prior)
    {
        var a=new Aggregate{Requests=r.Count,Valid=r.Count(x=>x.Category=="valid_epic_json"),CategoryCounts=r.GroupBy(x=>x.Category).ToDictionary(g=>g.Key,g=>g.Count()),PerProxy=r.GroupBy(x=>x.Proxy).ToDictionary(g=>g.Key,g=>Mini(g)),PerInstrument=r.GroupBy(x=>x.Instrument).ToDictionary(g=>g.Key,g=>Mini(g))};a.ValidPercent=a.Requests==0?0:100d*a.Valid/a.Requests;var bytes=r.Sum(x=>x.BodyBytes);a.BytesPerWireSend=a.Requests==0?0:(double)bytes/a.Requests;a.WireSendsPerUsefulRequest=a.Valid==0?0:(double)a.Requests/a.Valid;a.BytesPerUsefulRequest=a.Valid==0?0:(double)bytes/a.Valid;a.UsefulPagesPerSecond=wall==0?0:a.Valid/wall;a.UsefulRowsPerSecond=wall==0?0:r.Where(x=>x.Category=="valid_epic_json").Sum(x=>x.EntryCount)/wall;a.LatencyP50Seconds=Percentile(r.Select(x=>x.ElapsedSeconds),.5);a.LatencyP95Seconds=Percentile(r.Select(x=>x.ElapsedSeconds),.95);a.LatencyP99Seconds=Percentile(r.Select(x=>x.ElapsedSeconds),.99);a.ConnectP50Seconds=Percentile(r.Select(x=>x.ConnectSeconds),.5);a.ConnectP95Seconds=Percentile(r.Select(x=>x.ConnectSeconds),.95);a.AppConnectP50Seconds=Percentile(r.Select(x=>x.AppConnectSeconds),.5);a.AppConnectP95Seconds=Percentile(r.Select(x=>x.AppConnectSeconds),.95);a.StartTransferP50Seconds=Percentile(r.Select(x=>x.StartTransferSeconds),.5);a.StartTransferP95Seconds=Percentile(r.Select(x=>x.StartTransferSeconds),.95);a.ConnectionCount=r.Sum(x=>x.ConnectionCount);if(a.UsefulPagesPerSecond>0){a.ProjectedNetworkSeconds=fullPages/a.UsefulPagesPerSecond;a.ProjectedNetworkDeltaPercentVsBaseline=prior>0?100*(a.ProjectedNetworkSeconds-prior)/prior:0;}foreach(var g in r.Where(x=>x.Category=="valid_epic_json").GroupBy(x=>x.ScopeHash)){var n=g.Select(x=>x.EntriesSha256).Distinct().Count();a.ScopeFingerprintVariants[g.Key]=n;if(n>1)a.MultiVariantScopeCount++;}return a;
    }
    static async Task<List<HealthResult>> HealthAsync(IEnumerable<string> urls){using var c=new HttpClient{Timeout=TimeSpan.FromSeconds(10)};var outp=new List<HealthResult>();foreach(var u in urls){var sw=Stopwatch.StartNew();var h=new HealthResult{Url=u};try{using var res=await c.GetAsync(u);h.Status=(int)res.StatusCode;_ = await res.Content.ReadAsByteArrayAsync();}catch(Exception ex){h.Error=ex.GetType().Name;}h.Seconds=sw.Elapsed.TotalSeconds;outp.Add(h);}return outp;}
    static bool HealthOk(IEnumerable<HealthResult> h)=>h.All(x=>x.Status==200);
    public static async Task<StageReport> RunStageAsync(Config cfg,Stage stage)
    {
        var scratch=Path.Combine(cfg.ScratchDir,stage.Name);if(Directory.Exists(scratch))Directory.Delete(scratch,true);Directory.CreateDirectory(scratch);var started=DateTimeOffset.UtcNow;var before=await HealthAsync(cfg.HealthUrls);activeCurl=0;peakActiveCurl=0;await using var sampler=new Sampler(scratch);var global=new RateGate(stage.GlobalRps);var endpoint=cfg.Proxies.Select(_=>new RateGate(stage.PerExitRps)).ToArray();var channels=cfg.Proxies.Select(_=>Channel.CreateUnbounded<int>()).ToArray();var results=new Result[stage.RequestCount];var workers=new List<Task>();using var cts=new CancellationTokenSource();
        for(var pi=0;pi<cfg.Proxies.Count;pi++){var pidx=pi;for(var w=0;w<stage.PerExitConcurrency;w++)workers.Add(Task.Run(async()=>{await foreach(var idx in channels[pidx].Reader.ReadAllAsync(cts.Token)){await endpoint[pidx].WaitAsync(cts.Token);await global.WaitAsync(cts.Token);results[idx]=await RunCurlAsync(cfg,stage,cfg.Workload[idx%cfg.Workload.Count],cfg.Proxies[pidx],idx,scratch,cts.Token);}}));}
        var sw=Stopwatch.StartNew();for(var i=0;i<stage.RequestCount;i++)await channels[i%cfg.Proxies.Count].Writer.WriteAsync(i);foreach(var ch in channels)ch.Writer.Complete();await Task.WhenAll(workers);sw.Stop();var resources=sampler.Finish(Interlocked.Read(ref peakActiveCurl));var after=await HealthAsync(cfg.HealthUrls);var list=results.ToList();var agg=AggregateResults(list,sw.Elapsed.TotalSeconds,cfg.FullScrapePages,cfg.PriorNetworkSeconds);var report=new StageReport{Stage=stage,StartedAtUtc=started.ToString("O"),FinishedAtUtc=DateTimeOffset.UtcNow.ToString("O"),WallSeconds=sw.Elapsed.TotalSeconds,EffectiveExits=cfg.Proxies.Count,WorkloadScopes=cfg.Workload.Count,DistinctInstruments=cfg.Workload.Select(x=>x.Instrument).Distinct().Order().ToList(),HealthBefore=before,HealthAfter=after,Aggregate=agg,Resources=resources,Results=list};if(!HealthOk(before)||!HealthOk(after))report.GateReasons.Add("public_or_service_health_failed");if(agg.CategoryCounts.GetValueOrDefault("rate_limited_429")>0)report.GateReasons.Add("http_429_observed");if(agg.CategoryCounts.GetValueOrDefault("json_auth_entitlement")>0)report.GateReasons.Add("auth_or_entitlement_signal");if(agg.ValidPercent<80)report.GateReasons.Add("valid_json_below_80_percent");if(agg.WireSendsPerUsefulRequest>1.25)report.GateReasons.Add("wire_amplification_above_1_25");report.GatePassed=report.GateReasons.Count==0;try{Directory.Delete(scratch,true);}catch{}report.Resources.ScratchBytesAfter=Directory.Exists(scratch)?Directory.EnumerateFiles(scratch,"*",SearchOption.AllDirectories).Sum(f=>new FileInfo(f).Length):0;report.Resources.ScratchFilesAfter=Directory.Exists(scratch)?Directory.EnumerateFiles(scratch,"*",SearchOption.AllDirectories).LongCount():0;return report;
    }
    public static async Task<int> MainAsync()
    {
        var input=await Console.In.ReadToEndAsync();var cfg=JsonSerializer.Deserialize<Config>(input,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??throw new InvalidOperationException("config missing");if(string.IsNullOrEmpty(cfg.AccessToken)||cfg.Proxies.Count==0||cfg.Workload.Count==0||cfg.Stages.Count==0)throw new InvalidOperationException("incomplete config");Directory.CreateDirectory(cfg.OutputDir);Directory.CreateDirectory(cfg.ScratchDir);var summaries=new List<object>();foreach(var stage in cfg.Stages){var report=await RunStageAsync(cfg,stage);var path=Path.Combine(cfg.OutputDir,stage.Name+".json");await File.WriteAllTextAsync(path,JsonSerializer.Serialize(report,JsonOptions)+Environment.NewLine);var cc=report.Aggregate.CategoryCounts;Console.WriteLine($"stage={stage.Name} requests={report.Aggregate.Requests} valid={report.Aggregate.Valid} valid_pct={report.Aggregate.ValidPercent:F2} useful_pages_s={report.Aggregate.UsefulPagesPerSecond:F2} p50={report.Aggregate.LatencyP50Seconds:F3} p95={report.Aggregate.LatencyP95Seconds:F3} p99={report.Aggregate.LatencyP99Seconds:F3} blocks={cc.GetValueOrDefault("cdn_non_json_403")} timeouts={cc.GetValueOrDefault("timeout")} http503={cc.GetValueOrDefault("http_503")} http429={cc.GetValueOrDefault("rate_limited_429")} wire_per_useful={report.Aggregate.WireSendsPerUsefulRequest:F4} projected_network_s={report.Aggregate.ProjectedNetworkSeconds:F0} delta_vs_baseline_pct={report.Aggregate.ProjectedNetworkDeltaPercentVsBaseline:F2} peak_mem={report.Resources.PeakMemoryBytes} peak_pids={report.Resources.PeakPids} gate={report.GatePassed.ToString().ToLowerInvariant()} artifact={path}");summaries.Add(new{stage=stage.Name,report.GatePassed,report.GateReasons,report.Aggregate.ValidPercent,report.Aggregate.UsefulPagesPerSecond,report.Aggregate.WireSendsPerUsefulRequest,report.Aggregate.ProjectedNetworkSeconds,report.Aggregate.CategoryCounts});if(!report.GatePassed){Console.WriteLine($"matrix_stop stage={stage.Name} reasons={string.Join(',',report.GateReasons)}");break;}if(stage.CooldownSeconds>0)await Task.Delay(TimeSpan.FromSeconds(stage.CooldownSeconds));}await File.WriteAllTextAsync(Path.Combine(cfg.OutputDir,"matrix-summary.json"),JsonSerializer.Serialize(summaries,JsonOptions)+Environment.NewLine);return 0;
    }
}

static class Program { public static Task<int> Main() => Runner.MainAsync(); }
