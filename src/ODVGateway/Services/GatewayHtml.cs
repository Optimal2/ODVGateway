using System.Net;

namespace ODVGateway.Services;

public static class GatewayHtml
{
    public static IResult StatusPage(string title, string message)
    {
        var encodedTitle = WebUtility.HtmlEncode(title);
        var encodedMessage = WebUtility.HtmlEncode(message);

        var html = $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>{{encodedTitle}}</title>
  <style>
    body {
      margin: 0;
      min-height: 100vh;
      display: grid;
      place-items: center;
      font: 15px/1.45 Arial, Helvetica, sans-serif;
      background: #f6f7f9;
      color: #222;
    }
    main {
      width: min(720px, calc(100vw - 32px));
      border: 1px solid #d6dae0;
      background: #fff;
      padding: 24px;
      box-shadow: 0 8px 24px rgba(0,0,0,.08);
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

        return Results.Content(html, "text/html; charset=utf-8");
    }
}
