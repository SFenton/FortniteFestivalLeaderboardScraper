// OAuthLoopbackModule.h — React Native native module for Windows OAuth
//
// Implements the Authorization Code flow for Epic Games on Windows by
// starting a loopback HTTP listener on 127.0.0.1:{port} and opening the
// authorization URL in the system browser.  Epic redirects back to localhost
// after the user authenticates, and the module captures the `code` query
// parameter from the callback request.
//
// This replaces the non-existent `react-native-app-auth-windows` package.

#pragma once

#include <NativeModules.h>

namespace FortniteFestivalRN {

REACT_MODULE(OAuthLoopback, L"OAuthLoopback")
struct OAuthLoopback {

  /// Opens `authUrl` in the system browser and starts a loopback HTTP
  /// listener on 127.0.0.1:`port`.  Resolves with the `code` query
  /// parameter from the OAuth callback, or rejects on timeout / error.
  ///
  /// @param authUrl         Full Epic authorization URL (with query params)
  /// @param port            Loopback port to listen on (e.g. 8400)
  /// @param timeoutSeconds  How long to wait for the callback (e.g. 120)
  REACT_METHOD(Authorize, L"authorize");
  void Authorize(
      std::string authUrl,
      int port,
      int timeoutSeconds,
      winrt::Microsoft::ReactNative::ReactPromise<std::string> promise) noexcept;
};

} // namespace FortniteFestivalRN
