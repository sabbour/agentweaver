using System.Text.Encodings.Web;

namespace Agentweaver.Api.Endpoints;

internal enum AuthDialogTone
{
    Info,
    Success,
    Pending,
    Warning,
    Error,
}

internal sealed record AuthDialogPageModel(
    string PageTitle,
    string Heading,
    string Message,
    AuthDialogTone Tone = AuthDialogTone.Info,
    string? ContentHtml = null,
    string? ActionsHtml = null,
    string? Footer = null,
    string? StyleNonce = null,
    string? ScriptNonce = null);

internal static class AuthDialogPage
{
    internal static string Encode(string value) => HtmlEncoder.Default.Encode(value);

    internal static string Link(string label, string href, bool primary = false) =>
        $"<a class=\"button{(primary ? " primary" : "")}\" href=\"{Encode(href)}\">{Encode(label)}</a>";

    internal static string SubmitButton(string label, string name, string value, bool primary = false) =>
        $"<button{(primary ? " class=\"primary\"" : "")} type=\"submit\" name=\"{Encode(name)}\" value=\"{Encode(value)}\">{Encode(label)}</button>";

    internal static string Render(AuthDialogPageModel model)
    {
        var tone = model.Tone.ToString().ToLowerInvariant();
        var icon = model.Tone switch
        {
            AuthDialogTone.Success => "✓",
            AuthDialogTone.Pending => "…",
            AuthDialogTone.Warning => "!",
            AuthDialogTone.Error => "×",
            _ => "i",
        };
        var role = model.Tone == AuthDialogTone.Error ? "alert" : "status";
        var ariaLive = model.Tone == AuthDialogTone.Error ? "assertive" : "polite";
        var styleNonce = string.IsNullOrWhiteSpace(model.StyleNonce)
            ? ""
            : $" nonce=\"{Encode(model.StyleNonce)}\"";
        var scriptNonce = string.IsNullOrWhiteSpace(model.ScriptNonce)
            ? ""
            : $" nonce=\"{Encode(model.ScriptNonce)}\"";
        var content = string.IsNullOrWhiteSpace(model.ContentHtml)
            ? ""
            : $"<div class=\"content-detail\">{model.ContentHtml}</div>";
        var actions = string.IsNullOrWhiteSpace(model.ActionsHtml)
            ? ""
            : $"<div class=\"actions\">{model.ActionsHtml}</div>";
        var footer = string.IsNullOrWhiteSpace(model.Footer)
            ? ""
            : $"<p class=\"footer\">{Encode(model.Footer)}</p>";
        var closeScript = string.IsNullOrWhiteSpace(model.ScriptNonce)
            ? ""
            : $$"""
              <script{{scriptNonce}}>
                if (window.location.search || window.location.hash) {
                  window.history.replaceState(null, '', window.location.pathname);
                }
                document.getElementById('close-window')?.addEventListener('click', function () {
                  window.close();
                  window.setTimeout(function () {
                    document.getElementById('close-help')?.removeAttribute('hidden');
                  }, 150);
                });
              </script>
              """;

        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <meta name="color-scheme" content="light">
              <title>{{Encode(model.PageTitle)}} | Agentweaver</title>
              <style{{styleNonce}}>
                :root { color-scheme: light; font-family: "Segoe UI", "Segoe UI Web (West European)", -apple-system, BlinkMacSystemFont, Roboto, "Helvetica Neue", sans-serif; }
                * { box-sizing: border-box; }
                body { min-height: 100vh; margin: 0; padding: 32px 20px; display: grid; place-items: center; background: #f3f1ed; color: #242424; line-height: 1.45; -webkit-font-smoothing: antialiased; }
                .card { width: min(560px, 100%); overflow: hidden; background: #fcfcfa; border: 1px solid #dedede; border-radius: 14px; box-shadow: 0 8px 24px rgb(0 0 0 / 12%); }
                .content { padding: 32px; }
                .brand { display: flex; align-items: center; gap: 10px; margin-bottom: 28px; font-size: 16px; font-weight: 600; }
                .brand-mark { width: 28px; height: 28px; display: block; object-fit: contain; }
                .status-row { display: grid; grid-template-columns: 42px 1fr; gap: 14px; align-items: start; }
                .status-icon { width: 42px; height: 42px; display: grid; place-items: center; border-radius: 50%; font-size: 22px; font-weight: 700; }
                .tone-info .status-icon { background: #e8f1fb; color: #0f548c; }
                .tone-success .status-icon { background: #e8f5ed; color: #107c41; }
                .tone-pending .status-icon { background: #fff4ce; color: #8a5d00; }
                .tone-warning .status-icon { background: #fff4ce; color: #8a5d00; }
                .tone-error .status-icon { background: #fde7e9; color: #a4262c; }
                h1 { margin: 2px 0 0; font-size: 24px; line-height: 1.25; font-weight: 600; letter-spacing: -.02em; }
                .message { margin: 10px 0 0; color: #3c3c3c; font-size: 15px; }
                .content-detail { margin-top: 24px; }
                .client { margin: 0; padding: 16px; background: #f3f1ed; border: 1px solid #e6e6e6; border-radius: 10px; }
                .label { display: block; margin-bottom: 4px; color: #707070; font-size: 12px; font-weight: 600; letter-spacing: .02em; text-transform: uppercase; }
                .client-name { display: block; font-size: 17px; font-weight: 600; overflow-wrap: anywhere; }
                .client-id { display: block; margin-top: 4px; color: #707070; font: 12px/1.4 Consolas, "Courier New", monospace; overflow-wrap: anywhere; }
                h2 { margin: 24px 0 12px; font-size: 14px; font-weight: 600; }
                .permissions { display: grid; gap: 14px; margin: 0; padding: 0; list-style: none; }
                .permission { display: grid; grid-template-columns: 24px 1fr; gap: 10px; align-items: start; }
                .permission-icon { width: 20px; height: 20px; display: grid; place-items: center; margin-top: 1px; border-radius: 50%; background: #e8f5ed; color: #107c41; font-size: 12px; font-weight: 700; }
                .permission strong, .permission small, .permission code { display: block; }
                .permission strong { font-size: 14px; font-weight: 600; }
                .permission small { margin-top: 2px; color: #3c3c3c; font-size: 13px; }
                .permission code { width: fit-content; margin-top: 5px; padding: 2px 6px; border-radius: 4px; background: #f3f1ed; color: #707070; font: 11px/1.4 Consolas, "Courier New", monospace; }
                .identity { margin-top: 24px; padding-top: 18px; border-top: 1px solid #dedede; color: #3c3c3c; font-size: 13px; }
                .identity strong { display: block; margin-top: 3px; color: #242424; font-weight: 600; overflow-wrap: anywhere; }
                .actions { display: flex; justify-content: flex-end; flex-wrap: wrap; gap: 10px; padding: 20px 32px; background: #faf8f5; border-top: 1px solid #dedede; }
                .actions form { display: contents; }
                button, .button { min-width: 96px; min-height: 36px; display: inline-grid; place-items: center; padding: 8px 16px; border: 1px solid #c7c7c7; border-radius: 8px; background: #fcfcfa; color: #242424; font: 600 14px/1.2 inherit; cursor: pointer; text-decoration: none; }
                button:hover, .button:hover { background: #f3f1ed; border-color: #adadad; }
                button:active, .button:active { transform: translateY(1px); }
                button:focus-visible, .button:focus-visible { outline: 2px solid #242424; outline-offset: 2px; }
                .primary { border-color: #242424; background: #242424; color: #faf8f5; }
                .primary:hover { border-color: #3c3c3c; background: #3c3c3c; }
                .footer { margin: 20px 0 0; color: #707070; font-size: 13px; }
                #close-help { margin: 12px 0 0; color: #707070; font-size: 13px; }
                @media (max-width: 480px) { body { padding: 16px; } .content { padding: 24px; } .status-row { grid-template-columns: 36px 1fr; gap: 12px; } .status-icon { width: 36px; height: 36px; font-size: 19px; } .actions { padding: 18px 24px; } .actions > * { flex: 1; } }
                @media (prefers-reduced-motion: reduce) { *, *::before, *::after { scroll-behavior: auto !important; transition: none !important; } }
              </style>
            </head>
            <body>
              <main class="card tone-{{tone}}" aria-labelledby="dialog-title">
                <section class="content">
                  <div class="brand"><img class="brand-mark" src="/agentweaver.png" alt=""><span>Agentweaver</span></div>
                  <div class="status-row" role="{{role}}" aria-live="{{ariaLive}}">
                    <span class="status-icon" aria-hidden="true">{{icon}}</span>
                    <div>
                      <h1 id="dialog-title">{{Encode(model.Heading)}}</h1>
                      <p class="message">{{Encode(model.Message)}}</p>
                    </div>
                  </div>
                  {{content}}
                  {{footer}}
                </section>
                {{actions}}
              </main>
              {{closeScript}}
            </body>
            </html>
            """;
    }
}
