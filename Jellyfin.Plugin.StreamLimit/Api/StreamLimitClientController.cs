using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.StreamLimit.Api;

/// <summary>
/// Serves the small client-side script injected into the Jellyfin web client. The
/// script detects a stream-limit block and shows a single custom popup, suppressing
/// the web client's own "playback not allowed" dialog. Anonymous on purpose: it is
/// loaded by index.html before the user authenticates and contains no secrets.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("StreamLimit")]
public class StreamLimitClientController : ControllerBase
{
    /// <summary>
    /// Gets the injected client script, with the configured message baked in.
    /// </summary>
    /// <returns>The JavaScript content.</returns>
    [HttpGet("inject.js")]
    [Produces("application/javascript")]
    public IActionResult GetInjectScript()
    {
        // The fallback wording, used when a block response carries no per-block message
        // headers: placeholders render empty here, the real values come with each block.
        var configuration = Plugin.Instance?.Configuration;
        var titleJson = JsonSerializer.Serialize(MessageTemplate.Render(
            configuration?.ResolvedMessageTitle ?? Configuration.PluginConfiguration.DefaultMessageTitle,
            MessageValues.Empty));
        var textJson = JsonSerializer.Serialize(MessageTemplate.Render(
            configuration?.ResolvedMessageText ?? Configuration.PluginConfiguration.DefaultMessageText,
            MessageValues.Empty));

        // Don't cache: the message must reflect config changes without a hard reload.
        Response.Headers["Cache-Control"] = "no-store";
        return Content(BuildScript(titleJson, textJson), "application/javascript");
    }

    private static string BuildScript(string titleJson, string textJson)
    {
        // Injected global JS. jellyfin-web keeps its playback manager and its `alert`
        // module private (ES modules), so we cannot patch them. Instead we:
        //  1. patch XMLHttpRequest and window.fetch to detect our block (the PlaybackInfo
        //     POST goes through axios/XHR in older web builds, fetch in newer ones; we
        //     also tag every block response with X-StreamLimit),
        //  2. show one custom modal above everything (incl. the buffering spinner), with
        //     the block's own wording (placeholders resolved by the server) from the
        //     X-StreamLimit-Title / X-StreamLimit-Message headers when present, and
        //  3. run a short-lived MutationObserver that removes the native
        //     "Playback Error" dialog that jellyfin-web pops right after.
        // Raw string with $$ interpolation: single JS braces stay literal, only
        // {{titleJson}}/{{textJson}} are substituted.
        return $$"""
(function () {
  var TITLE = {{titleJson}};
  var TEXT = {{textJson}};
  var suppressUntil = 0;
  var lastShown = 0;

  // Header values are percent-encoded UTF-8; absent or undecodable -> null.
  function decodeHeader(value) {
    if (value === null || value === undefined || value === '') { return null; }
    try { return decodeURIComponent(value); } catch (e) { return null; }
  }

  function showModal(title, text) {
    var now = Date.now();
    // One popup per attempt: several requests (PlaybackInfo + follow-ups) can be
    // blocked in a burst; collapse them, and never stack two modals.
    if (now - lastShown < 2500 || document.getElementById('streamlimit-modal')) { return; }
    lastShown = now;
    try {
      var overlay = document.createElement('div');
      overlay.id = 'streamlimit-modal';
      overlay.setAttribute('style', 'position:fixed;inset:0;z-index:2147483647;display:flex;align-items:center;justify-content:center;background:rgba(0,0,0,.6);');
      var box = document.createElement('div');
      box.setAttribute('style', 'max-width:90vw;width:420px;background:#101010;color:#fff;border-radius:8px;padding:24px;box-shadow:0 8px 40px rgba(0,0,0,.5);font-family:inherit;');
      var h = document.createElement('h2');
      h.textContent = title !== null && title !== undefined ? title : TITLE;
      h.setAttribute('style', 'margin:0 0 12px;font-size:1.2em;');
      var p = document.createElement('div');
      p.textContent = text !== null && text !== undefined ? text : TEXT;
      p.setAttribute('style', 'margin:0 0 20px;opacity:.9;line-height:1.4;');
      var btn = document.createElement('button');
      btn.textContent = 'OK';
      btn.setAttribute('style', 'float:right;background:#00a4dc;color:#fff;border:0;border-radius:4px;padding:8px 20px;cursor:pointer;font-size:1em;');
      btn.onclick = function () { overlay.remove(); };
      box.appendChild(h); box.appendChild(p); box.appendChild(btn);
      overlay.appendChild(box);
      overlay.addEventListener('click', function (e) { if (e.target === overlay) { overlay.remove(); } });
      document.body.appendChild(overlay);
    } catch (e) { /* noop */ }
  }

  // Remove the client's own error dialog(s) while a block is being handled. Matches
  // the alert STRUCTURE (header title + centered content), not any wording, so it
  // works in every language and covers the several error dialogs a block can raise.
  function sweepDialogs() {
    if (Date.now() > suppressUntil) { return; }
    var removed = false;
    var containers = document.querySelectorAll('.dialogContainer');
    for (var i = 0; i < containers.length; i++) {
      var fd = containers[i].querySelector('.formDialog');
      if (fd && fd.querySelector('.formDialogHeaderTitle') && fd.querySelector('.dialog-content-centered')) {
        containers[i].remove();
        removed = true;
      }
    }
    if (removed) {
      var backs = document.querySelectorAll('.dialogBackdrop.dialogBackdropOpened');
      for (var j = 0; j < backs.length; j++) { backs[j].remove(); }
    }
  }

  // Newer jellyfin-web keeps its full-screen buffering spinner (.docspinner, z-index
  // 9999999) up after a refused playback; hide it so the page is usable again.
  function hideSpinner() {
    try {
      var spinners = document.querySelectorAll('.docspinner.mdlSpinnerActive');
      for (var i = 0; i < spinners.length; i++) { spinners[i].classList.remove('mdlSpinnerActive'); }
    } catch (e) { /* noop */ }
  }

  function trigger(title, text) {
    suppressUntil = Date.now() + 6000;
    showModal(title, text);
    hideSpinner();
    sweepDialogs();
  }

  try {
    var observer = new MutationObserver(sweepDialogs);
    observer.observe(document.documentElement, { childList: true, subtree: true });
  } catch (e) { /* noop */ }

  // Detect the block on the network layer (PlaybackInfo body or X-StreamLimit header).
  try {
    var open = XMLHttpRequest.prototype.open;
    var send = XMLHttpRequest.prototype.send;
    XMLHttpRequest.prototype.open = function (method, url) {
      this.__slUrl = url;
      return open.apply(this, arguments);
    };
    XMLHttpRequest.prototype.send = function () {
      var xhr = this;
      xhr.addEventListener('load', function () {
        try {
          if (xhr.getResponseHeader && xhr.getResponseHeader('X-StreamLimit')) {
            trigger(decodeHeader(xhr.getResponseHeader('X-StreamLimit-Title')), decodeHeader(xhr.getResponseHeader('X-StreamLimit-Message')));
            return;
          }
          if (/\/PlaybackInfo/i.test(xhr.__slUrl || '')) {
            var body = JSON.parse(xhr.responseText || '{}');
            if (body && body.ErrorCode === 'NotAllowed') { trigger(null, null); }
          }
        } catch (e) { /* noop */ }
      });
      return send.apply(this, arguments);
    };
  } catch (e) { /* noop */ }

  // Same detection for fetch(). The check runs on a clone, detached from the page's
  // promise chain: the caller always gets the original response (or rejection).
  try {
    if (typeof window.fetch === 'function') {
      var origFetch = window.fetch;
      window.fetch = function (input, init) {
        var promise = origFetch.apply(this, arguments);
        try {
          var url = typeof input === 'string' ? input : ((input && input.url) || String(input || ''));
          promise.then(function (res) {
            try {
              if (!res || !res.headers) { return; }
              if (res.headers.get('X-StreamLimit')) {
                trigger(decodeHeader(res.headers.get('X-StreamLimit-Title')), decodeHeader(res.headers.get('X-StreamLimit-Message')));
                return;
              }
              if (/\/PlaybackInfo/i.test(url)) {
                res.clone().json().then(function (body) {
                  if (body && body.ErrorCode === 'NotAllowed') { trigger(null, null); }
                }, function () { /* noop */ });
              }
            } catch (e) { /* noop */ }
          }, function () { /* noop */ });
        } catch (e) { /* noop */ }
        return promise;
      };
    }
  } catch (e) { /* noop */ }
})();
""";
    }
}
