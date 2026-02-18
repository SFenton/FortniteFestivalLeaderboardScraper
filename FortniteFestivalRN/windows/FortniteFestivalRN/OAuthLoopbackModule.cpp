// OAuthLoopbackModule.cpp — Windows OAuth loopback HTTP listener
//
// Background thread flow:
//   1. Bind a TCP socket to 127.0.0.1:{port}
//   2. Open the authorization URL in the default browser (ShellExecuteW)
//   3. Wait for a single HTTP request (with timeout via select())
//   4. Parse `code` or `error` from the callback query string
//   5. Serve a success / error HTML page to the browser
//   6. Resolve or reject the JS promise with the authorization code

#include "pch.h"
#include "OAuthLoopbackModule.h"

// pch.h defines WIN32_LEAN_AND_MEAN, so winsock2.h is safe to include
// after windows.h without redefinition conflicts.
#include <winsock2.h>
#include <ws2tcpip.h>
#pragma comment(lib, "ws2_32.lib")

#include <shellapi.h>
#include <thread>
#include <string>
#include <sstream>
#include <unordered_map>

namespace FortniteFestivalRN {

// ── URL-decode (%XX → char, + → space) ──────────────────────────

static std::string UrlDecode(const std::string &src) {
  std::string out;
  out.reserve(src.size());
  for (size_t i = 0; i < src.size(); ++i) {
    if (src[i] == '%' && i + 2 < src.size()) {
      unsigned int val = 0;
      if (sscanf_s(src.c_str() + i + 1, "%02x", &val) == 1) {
        out += static_cast<char>(val);
        i += 2;
        continue;
      }
    }
    if (src[i] == '+') {
      out += ' ';
      continue;
    }
    out += src[i];
  }
  return out;
}

// ── Parse a query string into key=value pairs ───────────────────

static std::unordered_map<std::string, std::string>
ParseQueryString(const std::string &qs) {
  std::unordered_map<std::string, std::string> params;
  std::istringstream stream(qs);
  std::string pair;
  while (std::getline(stream, pair, '&')) {
    auto eq = pair.find('=');
    if (eq == std::string::npos)
      continue;
    params[pair.substr(0, eq)] = UrlDecode(pair.substr(eq + 1));
  }
  return params;
}

// ── Extract query params from the first HTTP request line ───────
// Expects:  "GET /auth/callback?code=XYZ&state=... HTTP/1.1\r\n..."

static std::unordered_map<std::string, std::string>
ParseRequestLine(const std::string &request) {
  auto lineEnd = request.find('\r');
  std::string line =
      (lineEnd != std::string::npos) ? request.substr(0, lineEnd) : request;

  auto qPos = line.find('?');
  if (qPos == std::string::npos)
    return {};

  auto spacePos = line.find(' ', qPos);
  std::string qs = (spacePos != std::string::npos)
                       ? line.substr(qPos + 1, spacePos - qPos - 1)
                       : line.substr(qPos + 1);

  return ParseQueryString(qs);
}

// ── HTML response pages ─────────────────────────────────────────

static const char *kSuccessResponse =
    "HTTP/1.1 200 OK\r\n"
    "Content-Type: text/html; charset=utf-8\r\n"
    "Connection: close\r\n"
    "\r\n"
    "<!DOCTYPE html><html><head><title>Login Successful</title>"
    "<style>"
    "body{font-family:system-ui,-apple-system,sans-serif;"
    "display:flex;justify-content:center;align-items:center;height:100vh;"
    "margin:0;background:#1a1a2e;color:#fff}"
    ".card{text-align:center;padding:2rem 3rem;border-radius:12px;"
    "background:#16213e;box-shadow:0 8px 32px rgba(0,0,0,.4)}"
    "h1{color:#4ecca3;margin-bottom:.5rem}"
    "p{color:#a0a0c0;margin-top:0}"
    "</style></head>"
    "<body><div class=\"card\">"
    "<h1>&#10003; Login Successful</h1>"
    "<p>You can close this tab and return to the app.</p>"
    "</div></body></html>";

static const char *kErrorResponse =
    "HTTP/1.1 400 Bad Request\r\n"
    "Content-Type: text/html; charset=utf-8\r\n"
    "Connection: close\r\n"
    "\r\n"
    "<!DOCTYPE html><html><head><title>Login Failed</title>"
    "<style>"
    "body{font-family:system-ui,-apple-system,sans-serif;"
    "display:flex;justify-content:center;align-items:center;height:100vh;"
    "margin:0;background:#1a1a2e;color:#fff}"
    ".card{text-align:center;padding:2rem 3rem;border-radius:12px;"
    "background:#16213e;box-shadow:0 8px 32px rgba(0,0,0,.4)}"
    "h1{color:#e74c3c;margin-bottom:.5rem}"
    "p{color:#a0a0c0;margin-top:0}"
    "</style></head>"
    "<body><div class=\"card\">"
    "<h1>&#10007; Login Failed</h1>"
    "<p>Something went wrong. Please close this tab and try again.</p>"
    "</div></body></html>";

// ── Authorize implementation ────────────────────────────────────

void OAuthLoopback::Authorize(
    std::string authUrl, int port, int timeoutSeconds,
    winrt::Microsoft::ReactNative::ReactPromise<std::string> promise) noexcept {

  // Run on a detached background thread so we don't block JS/UI.
  std::thread(
      [authUrl = std::move(authUrl), port, timeoutSeconds,
       promise = std::move(promise)]() mutable {

        // ── 1. Initialize Winsock ────────────────────────────────
        WSADATA wsaData{};
        if (WSAStartup(MAKEWORD(2, 2), &wsaData) != 0) {
          promise.Reject("OAuthLoopback: failed to initialize Winsock");
          return;
        }

        // ── 2. Create & bind listening socket ────────────────────
        SOCKET listenSock =
            socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
        if (listenSock == INVALID_SOCKET) {
          WSACleanup();
          promise.Reject("OAuthLoopback: failed to create socket");
          return;
        }

        // Allow reuse so a quick restart doesn't fail with EADDRINUSE.
        int opt = 1;
        setsockopt(listenSock, SOL_SOCKET, SO_REUSEADDR,
                   reinterpret_cast<const char *>(&opt), sizeof(opt));

        sockaddr_in addr{};
        addr.sin_family = AF_INET;
        addr.sin_port = htons(static_cast<u_short>(port));
        inet_pton(AF_INET, "127.0.0.1", &addr.sin_addr);

        if (bind(listenSock, reinterpret_cast<sockaddr *>(&addr),
                 sizeof(addr)) == SOCKET_ERROR) {
          closesocket(listenSock);
          WSACleanup();
          promise.Reject(
              ("OAuthLoopback: port " + std::to_string(port) +
               " already in use")
                  .c_str());
          return;
        }

        if (listen(listenSock, 1) == SOCKET_ERROR) {
          closesocket(listenSock);
          WSACleanup();
          promise.Reject("OAuthLoopback: listen failed");
          return;
        }

        // ── 3. Open the authorization URL in the system browser ──
        std::wstring wUrl(authUrl.begin(), authUrl.end());
        ShellExecuteW(nullptr, L"open", wUrl.c_str(), nullptr, nullptr,
                      SW_SHOWNORMAL);

        // ── 4. Wait for the callback request (with timeout) ─────
        fd_set readSet;
        FD_ZERO(&readSet);
        FD_SET(listenSock, &readSet);

        timeval tv{};
        tv.tv_sec = timeoutSeconds;
        tv.tv_usec = 0;

        int sel = select(0 /*ignored on Windows*/, &readSet, nullptr,
                         nullptr, &tv);
        if (sel <= 0) {
          closesocket(listenSock);
          WSACleanup();
          auto errMsg =
              sel == 0
                  ? "OAuthLoopback: login timed out after " +
                        std::to_string(timeoutSeconds) + "s"
                  : std::string("OAuthLoopback: select() error");
          promise.Reject(errMsg.c_str());
          return;
        }

        SOCKET clientSock = accept(listenSock, nullptr, nullptr);
        closesocket(listenSock); // done listening

        if (clientSock == INVALID_SOCKET) {
          WSACleanup();
          promise.Reject("OAuthLoopback: accept failed");
          return;
        }

        // ── 5. Read the HTTP request ────────────────────────────
        char buf[4096]{};
        int bytesRead = recv(clientSock, buf, sizeof(buf) - 1, 0);
        if (bytesRead <= 0) {
          closesocket(clientSock);
          WSACleanup();
          promise.Reject("OAuthLoopback: failed to read callback");
          return;
        }
        buf[bytesRead] = '\0';
        std::string request(buf);

        // ── 6. Parse code / error from the callback ─────────────
        auto params = ParseRequestLine(request);
        auto codeIt = params.find("code");
        auto errorIt = params.find("error");

        if (codeIt != params.end() && !codeIt->second.empty()) {
          send(clientSock, kSuccessResponse,
               static_cast<int>(strlen(kSuccessResponse)), 0);
          // Brief delay so the browser receives the full response
          // before we close the socket.
          shutdown(clientSock, SD_SEND);
          closesocket(clientSock);
          WSACleanup();
          promise.Resolve(codeIt->second);
        } else {
          send(clientSock, kErrorResponse,
               static_cast<int>(strlen(kErrorResponse)), 0);
          shutdown(clientSock, SD_SEND);
          closesocket(clientSock);
          WSACleanup();

          std::string msg =
              (errorIt != params.end())
                  ? "Epic login error: " + errorIt->second
                  : "No authorization code in callback";
          promise.Reject(msg.c_str());
        }
      })
      .detach();
}

} // namespace FortniteFestivalRN
