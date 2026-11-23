using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Petal.Recipes.Translation;

namespace Petal.Recipes.Study;

/// <summary>
/// A scene the model designed and wrote for one idea of a part: its title, the idea it teaches, and its page (elements,
/// a style and scripts). It runs in a sandboxed frame of its own: no address it can reach, nothing of the page or the
/// browser it can read; the page lets in only the scripts it carries, by their hashes.
/// </summary>
public sealed record DesignedScene(string Title, string Idea, string Html, string Where)
{
    /// <summary>Its scripts' text, each as it runs: the page's policy names their hashes.</summary>
    public IReadOnlyList<string> Scripts => StudyDesigner.ScriptsOf(Html);
}

/// <summary>
/// Scenes the model designs for a part, with the user's own API key: each one teaches an idea by letting the reader do
/// something by hand and see it at once, drawn and moved by code the model writes for that idea alone (particles that
/// gather into a star, two boxes pushed with sliders, a satellite to put in its orbit). What the model writes is
/// checked: no address, no storage, no frames, no event attributes; and on the page it runs sandboxed, so even a scene
/// that slips through can reach nothing.
/// </summary>
public static partial class StudyDesigner
{
    /// <summary>The longest a scene may be.</summary>
    public const int Longest = 40000;

    internal static int Count(StudyScenes amount) => amount == StudyScenes.Many ? 2 : 1;

    internal static string Instruction(string into, string from, StudyScenes amount, string request, StudyPurpose purpose)
    {
        int n = Count(amount);
        var language = Translator.EnglishName(into);
        bool rtl = into is "ar" or "fa" or "ur";
        var translating = from != into && Translator.Languages.Any(l => l.Code == from)
            ? $" The part is in {Translator.EnglishName(from)}: put its words into {language}, with a technical term's own word in brackets the first time where it helps."
            : "";
        var sb = new StringBuilder();
        sb.Append("You are a brilliant teacher and a creative front-end developer. You make small interactive scenes for a study page: each one teaches one idea of the part by letting the reader do something with their own hands and see the result at once. ");
        sb.Append("They move, drag, launch, mix, build, aim, sort, slide or choose, and the scene answers with animation (canvas, SVG or CSS) and with words that say what is happening and why. ");
        sb.Append("Think of what the best science museum exhibit or a beautiful educational game would do for this very idea: dust that gathers into a star when the reader presses a button, two boxes pushed with sliders whose work bars grow, a ball whose energy bars change as it rolls down a slope, a satellite the reader puts in the right orbit, a mission whose vehicle the reader picks. A scene is never a quiz, a list or a text to read.\n\n");
        sb.Append(StudyPurposes.Reader(purpose)).Append('\n');
        sb.Append($"Write every word the reader sees in {language}.{translating}\n");
        sb.Append("Keep to the part: every fact, name, number, rule and result must be what it says or follows plainly from it. An everyday comparison may be made up, if it agrees with the part.\n");
        sb.Append(StudyFile.Requested(request, into));
        sb.Append($"\nMake {n} scene{(n == 1 ? "" : "s")}, for the {(n == 1 ? "most important idea" : "two most important ideas")} of the part. Each in exactly this form:\n\n");
        sb.Append("SCENE: <a short title, in ").Append(language).Append(">\nIDEA: <the idea it teaches, in one sentence, in ").Append(language).Append(">\n<html>\n<the scene: its elements, one <style> and one <script>>\n</html>\n\n");
        sb.Append("The scene is a fragment of HTML that goes inside a page's body, which is already ");
        sb.Append(rtl ? "right to left (dir=\"rtl\"): use inline-start and inline-end in CSS, and mirror what moves along a line" : "left to right");
        sb.Append(". Its rules:\n");
        sb.Append("- Everything is in it: no address of any kind (no http, no //, no src or href to a file), no fonts, images or libraries from anywhere, no import, fetch or XMLHttpRequest. Draw with canvas, SVG, CSS and Unicode symbols; a picture may be an inline SVG.\n");
        sb.Append("- No storage (localStorage, sessionStorage, cookies, indexedDB), no alert, confirm or prompt, no iframe, form, link, meta or base element.\n");
        sb.Append("- No event attributes such as onclick=\"…\": they won't run. Attach events in the script with addEventListener; a <script> has no attributes. Wrap the script in (function () { … })(); and start it with 'use strict';.\n");
        sb.Append("- It works with a mouse, a finger and the keyboard (buttons are <button> elements), from 320 to 760 pixels wide and no more than 520 pixels high; canvas drawings follow the width and devicePixelRatio.\n");
        sb.Append("- Use the page's colours, which change with light and dark: var(--bg), var(--surface), var(--text), var(--muted), var(--accent), var(--good), var(--bad), var(--line); bright colours of your own only for what is drawn.\n");
        sb.Append("- Text at least 14px; a short line telling the reader what to do; a reset or again button; and when the reader reaches the goal, a sentence saying what they just saw.\n");
        sb.Append("- When the reader asks for less motion (prefers-reduced-motion), jump to the end of an animation instead of playing it.\n");
        sb.Append("- At most 14000 characters a scene. Finish every scene: never stop in the middle.");
        return sb.ToString();
    }

    [GeneratedRegex(@"^[\s#*_>]*SCENE\s*:\s*(?<t>.+?)\s*\n[\s#*_>]*IDEA\s*:\s*(?<i>.+?)\s*\n(?<rest>.*?)(?=^[\s#*_>]*SCENE\s*:|\z)", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex SceneBlock();

    [GeneratedRegex(@"<html>\s*(?<h>.*?)\s*</html>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex HtmlBlock();

    [GeneratedRegex(@"```(?:html)?\s*\n(?<h>.*?)\n\s*```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Fenced();

    /// <summary>What a scene may not hold: anything that reaches out, keeps something, or runs outside its own scripts.</summary>
    [GeneratedRegex(@"https?:|(?:src|href|action|data|poster|srcset)\s*=\s*[""']?\s*//|<\s*(?:iframe|frame|object|embed|form|link|meta|base|portal|applet)\b|@import|url\(\s*['""]?\s*(?:https?:|//)|\bfetch\s*\(|XMLHttpRequest|WebSocket|EventSource|sendBeacon|importScripts|new\s+Worker|SharedWorker|serviceWorker|window\.open|document\.cookie|localStorage|sessionStorage|indexedDB|document\.domain|\bimport\s*\(|\beval\s*\(|new\s+Function|<[a-z][^>""'{}();]*\son[a-z]+\s*=|javascript:|<\s*script[^>]*\bsrc\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex Forbidden();

    [GeneratedRegex(@"<script\b[^>]*>(?<s>.*?)</script\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptElement();

    /// <summary>The scenes of a reply, each checked; none when it has none whole and safe.</summary>
    public static IReadOnlyList<DesignedScene> Read(string reply, string where)
    {
        var scenes = new List<DesignedScene>();
        foreach (Match block in SceneBlock().Matches(reply.Replace("\r\n", "\n")))
        {
            var rest = block.Groups["rest"].Value;
            var html = HtmlBlock().Match(rest) is { Success: true } h ? h.Groups["h"].Value : Fenced().Match(rest) is { Success: true } f ? f.Groups["h"].Value : "";
            html = Fenced().Match(html) is { Success: true } inner ? inner.Groups["h"].Value : html;
            if (Checked(html) is not { } safe)
                continue;
            var title = StudyFile.Plainly(block.Groups["t"].Value.Trim().Trim('<', '>'));
            var idea = StudyFile.Plainly(block.Groups["i"].Value.Trim().Trim('<', '>'));
            if (title.Length is 0 or > 100 || idea.Length is 0 or > 400)
                continue;
            scenes.Add(new DesignedScene(title, idea, safe, where));
        }
        return scenes;
    }

    /// <summary>
    /// A scene as it may run: whole (every script closed, not cut off), within its length, holding nothing that reaches
    /// out or keeps anything, its scripts without attributes. Null when it isn't.
    /// </summary>
    internal static string? Checked(string html)
    {
        html = html.Trim();
        if (html.Length is < 200 or > Longest)
            return null;
        if (Regex.IsMatch(html, @"^\s*<!doctype|<\s*/?\s*(?:html|head|body)\b", RegexOptions.IgnoreCase))
            html = Regex.Replace(Regex.Replace(html, @"<!doctype[^>]*>|<\s*/?\s*(?:html|body)\b[^>]*>", "", RegexOptions.IgnoreCase), @"<\s*/?\s*head\b[^>]*>", "", RegexOptions.IgnoreCase).Trim();
        int opened = Regex.Matches(html, @"<script\b", RegexOptions.IgnoreCase).Count;
        int closed = Regex.Matches(html, @"</script\s*>", RegexOptions.IgnoreCase).Count;
        if (opened == 0 || opened != closed || opened > 3)
            return null;
        if (Forbidden().IsMatch(html))
            return null;
        // A script's type, if any, goes: the page's policy lets in a plain script by its hash.
        html = Regex.Replace(html, @"<script\b[^>]*>", "<script>", RegexOptions.IgnoreCase);
        var scripts = ScriptsOf(html);
        // A script cut off in the middle doesn't run: its brackets don't close.
        if (scripts.Any(s => !Balanced(s)))
            return null;
        return html;
    }

    internal static IReadOnlyList<string> ScriptsOf(string html) =>
        ScriptElement().Matches(html).Select(m => m.Groups["s"].Value).ToList();

    /// <summary>Whether a script's brackets close, outside its strings, comments and patterns: a scene cut off never does.</summary>
    internal static bool Balanced(string script)
    {
        var stack = new Stack<char>();
        for (int i = 0; i < script.Length; i++)
        {
            char c = script[i];
            if (c is '"' or '\'' or '`')
            {
                for (i++; i < script.Length && script[i] != c; i++)
                    if (script[i] == '\\')
                        i++;
                continue;
            }
            if (c == '/' && i + 1 < script.Length && script[i + 1] == '/')
            {
                while (i < script.Length && script[i] != '\n')
                    i++;
                continue;
            }
            if (c == '/' && i + 1 < script.Length && script[i + 1] == '*')
            {
                int end = script.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                    return false;
                i = end + 1;
                continue;
            }
            if (c is '(' or '[' or '{')
                stack.Push(c);
            else if (c is ')' or ']' or '}')
            {
                if (stack.Count == 0 || stack.Pop() != (c == ')' ? '(' : c == ']' ? '[' : '{'))
                    return false;
            }
        }
        return stack.Count == 0;
    }

    /// <summary>What the scene is given: the part's place, its explanation as written, and its own words.</summary>
    internal static string Given(string chapter, string title, IEnumerable<string> explained, string text, int most)
    {
        var sb = new StringBuilder();
        if (chapter.Length > 0 && chapter != title)
            sb.Append("Chapter: ").Append(chapter).Append('\n');
        if (title.Length > 0)
            sb.Append("Part: ").Append(title).Append("\n\n");
        var explanation = string.Join("\n\n", explained.Where(e => e.Length > 0));
        if (explanation.Length > 0)
            sb.Append("The part as it is explained on the page:\n").Append(explanation).Append("\n\n");
        sb.Append("The part's own words:\n").Append(text);
        var all = sb.ToString();
        return all.Length > most ? all[..most] : all;
    }

    // ───────────── on the page ─────────────

    /// <summary>
    /// The little script every scene's frame begins with: it tells the page how tall the scene is and when it failed,
    /// and takes the page's light or dark. A failed scene gives way to its idea in words.
    /// </summary>
    public const string Bridge =
        "(function(){'use strict';var d=document,r=d.documentElement;" +
        "function say(m){try{parent.postMessage(m,'*');}catch(e){}}" +
        "function size(){if(d.body){say({dw:'h',h:Math.ceil(d.body.getBoundingClientRect().height)});}}" +
        "window.addEventListener('error',function(){say({dw:'fail'});});" +
        "window.addEventListener('unhandledrejection',function(){say({dw:'fail'});});" +
        "window.addEventListener('message',function(e){if(e.data&&e.data.dw==='theme'){r.setAttribute('data-theme',e.data.theme);}});" +
        "d.addEventListener('DOMContentLoaded',function(){size();if(window.ResizeObserver){new ResizeObserver(size).observe(d.body);}say({dw:'ready'});});" +
        "window.addEventListener('load',size);})();";

    /// <summary>The scene's own page's colours and type, light and dark, as the study page has them.</summary>
    private const string Base =
        ":root{--bg:#f7f6f2;--surface:#fff;--raised:#fbfaf8;--text:#1d1d1f;--muted:#66666d;--line:#e4e1da;--accent:#2459d6;--accent-ink:#fff;--soft:#e9effd;--good:#16803c;--bad:#c62828;color-scheme:light}" +
        ":root[data-theme=dark]{--bg:#121315;--surface:#1b1c1f;--raised:#202125;--text:#ececf0;--muted:#a3a3ad;--line:#2e2f34;--accent:#82a7ff;--accent-ink:#0d1424;--soft:#1d2740;--good:#5fd18a;--bad:#ff8a80;color-scheme:dark}" +
        "*{box-sizing:border-box}html,body{margin:0;background:transparent;color:var(--text);font:16px/1.55 system-ui,-apple-system,'Segoe UI',Roboto,'Noto Sans','Noto Sans Arabic',Tahoma,sans-serif}" +
        "body{padding:4px 2px 8px}button{font:inherit;cursor:pointer}canvas,svg{max-width:100%}";

    /// <summary>The scene as a page of its own, for its sandboxed frame.</summary>
    public static string Document(DesignedScene scene, string language)
    {
        bool rtl = language is "ar" or "fa" or "ur";
        return "<!doctype html><html lang=\"" + language + "\" dir=\"" + (rtl ? "rtl" : "ltr") + "\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><style>"
            + Base + "</style><script>" + Bridge + "</script></head><body>" + scene.Html + "</body></html>";
    }

    /// <summary>The hash a page's policy names a script by.</summary>
    public static string Hash(string script) => "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script))) + "'";

    /// <summary>The frame a scene runs in: scripts only, nothing of the page's origin, its document given whole.</summary>
    public static string Frame(DesignedScene scene, string language) =>
        "<iframe class=\"designed-frame\" sandbox=\"allow-scripts\" loading=\"lazy\" title=\"" + WebUtility.HtmlEncode(scene.Title) + "\" srcdoc=\"" + WebUtility.HtmlEncode(Document(scene, language)) + "\"></iframe>";
}
