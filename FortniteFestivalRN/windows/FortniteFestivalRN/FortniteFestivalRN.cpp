// FortniteFestivalRN.cpp : Defines the entry point for the application.
//

#include "pch.h"
#include "FortniteFestivalRN.h"

#include "AutolinkedNativeModules.g.h"

#include "NativeModules.h"

#include <winrt/Microsoft.UI.Windowing.h>
#include <winrt/Microsoft.UI.Interop.h>
#include <commctrl.h>
#pragma comment(lib, "comctl32.lib")
#include <string>
#include <Shlwapi.h>  // PathCchCombine is from pathcch.h, already available via pch

// ── Resize throttle ────────────────────────────────────────────────
// RNW's Fabric Composition renderer crashes (access violation in
// Microsoft.ReactNative.dll) when AppWindow.Changed → Arrange() fires
// faster than the layout engine can process each layout pass.
//
// We subclass the HWND to coalesce WM_SIZE messages during interactive
// window dragging (WM_ENTERSIZEMOVE / WM_EXITSIZEMOVE).  Other WM_SIZE
// sources (maximize, restore, programmatic Resize()) pass through
// immediately so the app still feels snappy for those operations.
// ---------------------------------------------------------------------------

static constexpr UINT_PTR RESIZE_SUBCLASS_ID = 1;
static constexpr UINT_PTR RESIZE_TIMER_ID = 42;
static constexpr UINT RESIZE_COALESCE_MS = 80;

struct ResizeThrottleState {
  WPARAM lastWParam{0};
  LPARAM lastLParam{0};
  bool pending{false};
  bool inSizeMove{false};
};

static LRESULT CALLBACK ResizeThrottleProc(
    HWND hWnd, UINT uMsg, WPARAM wParam, LPARAM lParam,
    UINT_PTR /*uIdSubclass*/, DWORD_PTR dwRefData) {
  auto *state = reinterpret_cast<ResizeThrottleState *>(dwRefData);

  switch (uMsg) {
    case WM_ENTERSIZEMOVE:
      state->inSizeMove = true;
      return DefSubclassProc(hWnd, uMsg, wParam, lParam);

    case WM_EXITSIZEMOVE:
      state->inSizeMove = false;
      // Immediately flush any pending size so the final size is applied.
      KillTimer(hWnd, RESIZE_TIMER_ID);
      if (state->pending) {
        state->pending = false;
        DefSubclassProc(hWnd, WM_SIZE, state->lastWParam, state->lastLParam);
      }
      return DefSubclassProc(hWnd, uMsg, wParam, lParam);

    case WM_SIZE:
      if (state->inSizeMove) {
        // During interactive drag — coalesce.
        state->lastWParam = wParam;
        state->lastLParam = lParam;
        state->pending = true;
        KillTimer(hWnd, RESIZE_TIMER_ID);
        SetTimer(hWnd, RESIZE_TIMER_ID, RESIZE_COALESCE_MS, nullptr);
        return 0; // suppress — don't let AppWindow fire Changed
      }
      // Not dragging (maximize, restore, programmatic, etc.) — pass through.
      break;

    case WM_TIMER:
      if (wParam == RESIZE_TIMER_ID) {
        KillTimer(hWnd, RESIZE_TIMER_ID);
        if (state->pending) {
          state->pending = false;
          return DefSubclassProc(hWnd, WM_SIZE, state->lastWParam, state->lastLParam);
        }
        return 0;
      }
      break;

    case WM_NCDESTROY:
      RemoveWindowSubclass(hWnd, ResizeThrottleProc, RESIZE_SUBCLASS_ID);
      delete state;
      return DefSubclassProc(hWnd, uMsg, wParam, lParam);
  }

  return DefSubclassProc(hWnd, uMsg, wParam, lParam);
}

// A PackageProvider containing any turbo modules you define within this app project
struct CompReactPackageProvider
    : winrt::implements<CompReactPackageProvider, winrt::Microsoft::ReactNative::IReactPackageProvider> {
 public: // IReactPackageProvider
  void CreatePackage(winrt::Microsoft::ReactNative::IReactPackageBuilder const &packageBuilder) noexcept {
    AddAttributedModules(packageBuilder, true);
  }
};

// The entry point of the Win32 application
_Use_decl_annotations_ int CALLBACK WinMain(HINSTANCE instance, HINSTANCE, PSTR /* commandLine */, int showCmd) {
  // Initialize WinRT
  winrt::init_apartment(winrt::apartment_type::single_threaded);

  // Enable per monitor DPI scaling
  SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

  // Find the path hosting the app exe file
  WCHAR appDirectory[MAX_PATH];
  GetModuleFileNameW(NULL, appDirectory, MAX_PATH);
  PathCchRemoveFileSpec(appDirectory, MAX_PATH);

  // Register bundled Ionicons font so react-native-vector-icons can render
  // glyphs using fontFamily: 'Ionicons'.  Flag 0 (instead of FR_PRIVATE)
  // adds the font to the session font table so it is visible to both GDI
  // and DirectWrite.  The Composition renderer uses DirectWrite exclusively;
  // FR_PRIVATE fonts are invisible to DirectWrite.
  //
  // Use SendNotifyMessage (not SendMessage) for the WM_FONTCHANGE broadcast
  // so that we don't block waiting for every window on the system to respond.
  {
    WCHAR fontPath[MAX_PATH];
    PathCchCombine(fontPath, MAX_PATH, appDirectory, L"Fonts\\Ionicons.ttf");
    int fontsAdded = AddFontResourceExW(fontPath, 0, 0);
    if (fontsAdded > 0) {
      SendNotifyMessage(HWND_BROADCAST, WM_FONTCHANGE, 0, 0);
    }
  }

  // Create a ReactNativeWin32App with the ReactNativeAppBuilder
  auto reactNativeWin32App{winrt::Microsoft::ReactNative::ReactNativeAppBuilder().Build()};

  // Configure the initial InstanceSettings for the app's ReactNativeHost
  auto settings{reactNativeWin32App.ReactNativeHost().InstanceSettings()};
  // Register any autolinked native modules
  RegisterAutolinkedNativeModulePackages(settings.PackageProviders());
  // Register any native modules defined within this app project
  settings.PackageProviders().Append(winrt::make<CompReactPackageProvider>());

#if BUNDLE
  // Load the JS bundle from a file (not Metro):
  // Set the path (on disk) where the .bundle file is located
  settings.BundleRootPath(std::wstring(L"file://").append(appDirectory).append(L"\\Bundle\\").c_str());
  // Set the name of the bundle file (without the .bundle extension)
  settings.JavaScriptBundleFile(L"index.windows");
  // Disable hot reload
  settings.UseFastRefresh(false);
#else
  // Load the JS bundle from Metro
  settings.JavaScriptBundleFile(L"index");
  // Enable hot reload
  settings.UseFastRefresh(true);
#endif
#if _DEBUG
  // For Debug builds
  // Enable Direct Debugging of JS
  settings.UseDirectDebugger(true);
  // Enable the Developer Menu
  settings.UseDeveloperSupport(true);
#else
  // For Release builds:
  // Disable Direct Debugging of JS
  settings.UseDirectDebugger(false);
  // Disable the Developer Menu
  settings.UseDeveloperSupport(false);
#endif

  // Get the AppWindow so we can configure its initial title and size
  auto appWindow{reactNativeWin32App.AppWindow()};
  appWindow.Title(L"FortniteFestivalRN");

  // Explicitly use an overlapped presenter and enable resizing.
  // If the presenter is not resizable, the React root view will not receive size changes.
  appWindow.SetPresenter(winrt::Microsoft::UI::Windowing::AppWindowPresenterKind::Overlapped);
  if (auto overlapped = appWindow.Presenter().try_as<winrt::Microsoft::UI::Windowing::OverlappedPresenter>()) {
    overlapped.IsResizable(true);
    overlapped.IsMaximizable(true);
    overlapped.IsMinimizable(true);
  }

  // ── Install resize throttle on the HWND ─────────────────────────
  // Must be done BEFORE Start() so the subclass intercepts WM_SIZE
  // before RNW's own AppWindow.Changed handler can call Arrange().
  {
    auto hwnd = winrt::Microsoft::UI::GetWindowFromWindowId(appWindow.Id());
    auto *state = new ResizeThrottleState();
    SetWindowSubclass(hwnd, ResizeThrottleProc, RESIZE_SUBCLASS_ID,
                      reinterpret_cast<DWORD_PTR>(state));
  }

  appWindow.Resize({1000, 1000});

  // Get the ReactViewOptions so we can set the initial RN component to load
  auto viewOptions{reactNativeWin32App.ReactViewOptions()};
  viewOptions.ComponentName(L"FortniteFestivalRN");

  // Start the app
  reactNativeWin32App.Start();
}
