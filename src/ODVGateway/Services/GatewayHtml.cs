using System.Net;

namespace ODVGateway.Services;

public static class GatewayHtml
{
    // The status/error pages are server-rendered, so the theme is applied from
    // the OMP_THEME_PREFERENCE cookie without any script: data-theme is the
    // palette in use, data-theme-mode is the user's choice (theme contract
    // version 1). "system" renders the light palette as the pre-paint fallback
    // and the CSS media query follows prefers-color-scheme; print always uses
    // the light palette. The inline <style> is covered by the gateway's
    // existing style-src 'self' 'unsafe-inline' CSP.
    public static IResult StatusPage(string title, string message, int statusCode, string? themeMode = null)
    {
        var encodedTitle = WebUtility.HtmlEncode(title);
        var encodedMessage = WebUtility.HtmlEncode(message);

        var (theme, mode) = themeMode switch
        {
            OmpThemePreference.LightMode => ("light", "light"),
            OmpThemePreference.DarkMode => ("dark", "dark"),
            _ => ("light", "system")
        };

        var html = $$"""
<!doctype html>
<html lang="en" data-theme="{{theme}}" data-theme-mode="{{mode}}">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>{{encodedTitle}}</title>
  <style>
    :root {
      color-scheme: light;
      --gw-bg: #f6f7f9;
      --gw-surface: #fff;
      --gw-text: #222;
      --gw-border: #d6dae0;
      --gw-shadow: rgba(0,0,0,.08);
    }
    html[data-theme="dark"] {
      color-scheme: dark;
      --gw-bg: #0f172a;
      --gw-surface: #111c2d;
      --gw-text: #e5edf7;
      --gw-border: #2a3648;
      --gw-shadow: rgba(0,0,0,.5);
    }
    @media (prefers-color-scheme: dark) {
      html[data-theme-mode="system"] {
        color-scheme: dark;
        --gw-bg: #0f172a;
        --gw-surface: #111c2d;
        --gw-text: #e5edf7;
        --gw-border: #2a3648;
        --gw-shadow: rgba(0,0,0,.5);
      }
    }
    @media print {
      :root, html[data-theme="dark"], html[data-theme-mode="system"] {
        color-scheme: light;
        --gw-bg: #fff;
        --gw-surface: #fff;
        --gw-text: #222;
        --gw-border: #d6dae0;
        --gw-shadow: none;
      }
    }
    body {
      margin: 0;
      min-height: 100vh;
      display: grid;
      place-items: center;
      font: 15px/1.45 Arial, Helvetica, sans-serif;
      background: var(--gw-bg);
      color: var(--gw-text);
    }
    main {
      width: min(720px, calc(100vw - 32px));
      border: 1px solid var(--gw-border);
      background: var(--gw-surface);
      padding: 24px;
      box-shadow: 0 8px 24px var(--gw-shadow);
    }
    h1 {
      font-size: 22px;
      margin: 0 0 12px;
    }
    p {
      margin: 0;
    }
  </style>
</head>
<body>
  <main>
    <h1>{{encodedTitle}}</h1>
    <p>{{encodedMessage}}</p>
  </main>
</body>
</html>
""";

        return Results.Content(html, "text/html; charset=utf-8", statusCode: statusCode);
    }
}
