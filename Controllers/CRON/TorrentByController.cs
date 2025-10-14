using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using JacRed.Engine.CORE;
using JacRed.Models.tParse;
using IO = System.IO;
using JacRed.Engine;
using JacRed.Models.Details;
using Microsoft.Extensions.Caching.Memory;
using System.Net.Http;
using System.Net;
using CoreHttp = JacRed.Engine.CORE.HttpClient;

namespace JacRed.Controllers.CRON
{
    [Route("/cron/torrentby/[action]")]
    public class TorrentByController : Controller
    {
        static Dictionary<string, List<TaskParse>> taskParse = new Dictionary<string, List<TaskParse>>();
        static readonly object taskLock = new object();

        static TorrentByController()
        {
            try
            {
                if (IO.File.Exists("Data/temp/torrentby_taskParse.json"))
                    taskParse = JsonConvert.DeserializeObject<Dictionary<string, List<TaskParse>>>(IO.File.ReadAllText("Data/temp/torrentby_taskParse.json"));
            }
            catch { }
        }

        readonly IMemoryCache memoryCache;
        static readonly object rndLock = new object();
        static readonly Random rnd = new Random();

        public TorrentByController(IMemoryCache memoryCache)
        {
            this.memoryCache = memoryCache;
        }

        #region Cookie / Login / Persist
        static readonly string CookiePath = "Data/temp/torrentby.cookie";

        static string NormalizeCookie(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var piece in raw.Split(';'))
            {
                var p = piece?.Trim();
                if (string.IsNullOrEmpty(p)) continue;
                var kv = p.Split(new[] { '=' }, 2);
                if (kv.Length < 2) continue;

                var name = kv[0].Trim();
                var val = kv[1].Trim().Trim('\"');

                // Skip Set-Cookie attributes
                if (name.Equals("path", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("domain", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("expires", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("max-age", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("secure", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("httponly", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("samesite", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (name.Length > 0)
                    dict[name] = val;
            }

            return string.Join("; ", dict.Select(kv => $"{kv.Key}={kv.Value}"));
        }

        static void SaveCookie(IMemoryCache memoryCache, string raw)
        {
            var norm = NormalizeCookie(raw);
            if (string.IsNullOrWhiteSpace(norm)) return;

            memoryCache.Set("torrentby:cookie", norm, DateTime.Now.AddDays(1));
            AppInit.conf.TorrentBy.cookie = norm;

            try
            {
                IO.Directory.CreateDirectory(IO.Path.GetDirectoryName(CookiePath));
                IO.File.WriteAllText(CookiePath, norm);
            }
            catch { }
        }

        static string LoadCookie(IMemoryCache memoryCache)
        {
            if (memoryCache.TryGetValue("torrentby:cookie", out string c) && !string.IsNullOrWhiteSpace(c))
                return c;

            if (!string.IsNullOrWhiteSpace(AppInit.conf.TorrentBy.cookie))
            {
                var n = NormalizeCookie(AppInit.conf.TorrentBy.cookie);
                if (!string.IsNullOrWhiteSpace(n))
                {
                    SaveCookie(memoryCache, n);
                    return n;
                }
            }

            try
            {
                if (IO.File.Exists(CookiePath))
                {
                    var fromFile = NormalizeCookie(IO.File.ReadAllText(CookiePath));
                    if (!string.IsNullOrWhiteSpace(fromFile))
                    {
                        SaveCookie(memoryCache, fromFile);
                        return fromFile;
                    }
                }
            }
            catch { }

            return null;
        }

        async Task<bool> EnsureLogin()
        {
            var cookie = LoadCookie(memoryCache);
            if (!string.IsNullOrEmpty(cookie))
                return true;

            return await TakeLogin();
        }

        async Task<bool> TakeLogin()
        {
            string authKey = "torrentby:TakeLogin()";
            if (memoryCache.TryGetValue(authKey, out _))
                return false;

            memoryCache.Set(authKey, 0, TimeSpan.FromMinutes(2));

            try
            {
                var handler = new HttpClientHandler { AllowAutoRedirect = false };
                handler.ServerCertificateCustomValidationCallback += (sender, cert, chain, sslPolicyErrors) => true;

                using (var client = new System.Net.Http.HttpClient(handler))
                {
                    client.Timeout = TimeSpan.FromSeconds(15);
                    client.DefaultRequestHeaders.Add("user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36");
                    client.DefaultRequestHeaders.Add("accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

                    var post = new Dictionary<string, string>
                    {
                        { "username", AppInit.conf.TorrentBy.login.u },
                        { "password", AppInit.conf.TorrentBy.login.p }
                    };

                    using (var content = new FormUrlEncodedContent(post))
                    {
                        using (var resp = await client.PostAsync($"{AppInit.conf.TorrentBy.host}/login/", content))
                        {
                            if (resp.Headers.TryGetValues("Set-Cookie", out var setCookies))
                            {
                                var pairs = setCookies
                                    .Select(sc => sc?.Split(';')?.FirstOrDefault()?.Trim())
                                    .Where(s => !string.IsNullOrWhiteSpace(s));
                                var combined = string.Join("; ", pairs);

                                SaveCookie(memoryCache, combined);
                                return true;
                            }
                        }
                    }
                }
            }
            catch { }

            return false;
        }
        #endregion

        #region Helpers
        static readonly string[] categories = new[] { "films", "movies", "serials", "tv", "humor", "cartoons", "anime", "sport" };

        static string[] MapTypes(string cat)
        {
            switch (cat)
            {
                case "films":
                case "movies":
                    return new[] { "movie" };
                case "serials":
                    return new[] { "serial" };
                case "tv":
                case "humor":
                    return new[] { "tvshow" };
                case "cartoons":
                    return new[] { "multfilm", "multserial" };
                case "anime":
                    return new[] { "anime" };
                case "sport":
                    return new[] { "sport" };
                default:
                    return Array.Empty<string>();
            }
        }

        async Task DelayWithJitter()
        {
            int baseMs = AppInit.conf.TorrentBy.parseDelay;
            if (baseMs <= 0) baseMs = 1000;
            int jitter;
            lock (rndLock) jitter = rnd.Next(250, 1250);
            await Task.Delay(baseMs + jitter);
        }

        static string HtmlDecode(string s) => string.IsNullOrEmpty(s) ? s : HttpUtility.HtmlDecode(s);
        static string StripTags(string s) => string.IsNullOrEmpty(s) ? s : Regex.Replace(s, "<.*?>", string.Empty);
        #endregion

        #region Parse (manual)
        static bool workParse = false;

        // /cron/torrentby/parse?page=1
        [HttpGet]
        public async Task<string> Parse(int page = 1)
        {
            if (workParse) return "work";
            workParse = true;
            var sb = new StringBuilder();

            try
            {
                await EnsureLogin();

                foreach (var cat in categories)
                {
                    bool ok = await parsePage(cat, page);
                    sb.AppendLine($"{cat} - {(ok ? "ok" : "empty")}");
                    await DelayWithJitter();
                }
            }
            catch { }
            finally
            {
                workParse = false;
            }

            return sb.Length == 0 ? "ok" : sb.ToString();
        }
        #endregion

        #region UpdateTasksParse (init + daily)
        static bool _taskWork = false;

        // /cron/torrentby/UpdateTasksParse
        [HttpGet]
        public async Task<string> UpdateTasksParse()
        {
            if (_taskWork) return "work";
            _taskWork = true;

            try
            {
                await EnsureLogin();

                // init pages plan if empty
                lock (taskLock)
                {
                    foreach (var cat in categories)
                    {
                        if (!taskParse.ContainsKey(cat))
                            taskParse[cat] = Enumerable.Range(1, 10).Select(p => new TaskParse(p)).ToList();
                    }
                }

                foreach (var kv in taskParse.ToArray())
                {
                    foreach (var tp in kv.Value.OrderBy(a => a.updateTime))
                    {
                        if (tp.updateTime.Date == DateTime.Today)
                            continue;

                        bool res = await parsePage(kv.Key, tp.page);
                        if (res)
                            tp.updateTime = DateTime.Today;

                        await DelayWithJitter();
                    }
                }
            }
            catch { }
            finally
            {
                _taskWork = false;
                try
                {
                    IO.Directory.CreateDirectory("Data/temp");
                    IO.File.WriteAllText("Data/temp/torrentby_taskParse.json", JsonConvert.SerializeObject(taskParse));
                }
                catch { }
            }

            return "ok";
        }
        #endregion

        #region ParseAllTask
        static bool _parseAllTaskWork = false;

        // /cron/torrentby/ParseAllTask
        [HttpGet]
        public async Task<string> ParseAllTask()
        {
            if (_parseAllTaskWork)
                return "work";

            _parseAllTaskWork = true;

            try
            {
                await EnsureLogin();

                foreach (var task in taskParse.ToArray())
                {
                    foreach (var val in task.Value.ToArray())
                    {
                        try
                        {
                            if (DateTime.Today == val.updateTime)
                                continue;

                            bool res = await parsePage(task.Key, val.page);
                            if (res)
                                val.updateTime = DateTime.Today;

                            await DelayWithJitter();
                        }
                        catch { }
                    }
                }
            }
            catch { }
            finally
            {
                _parseAllTaskWork = false;
                try
                {
                    IO.Directory.CreateDirectory("Data/temp");
                    IO.File.WriteAllText("Data/temp/torrentby_taskParse.json", JsonConvert.SerializeObject(taskParse));
                }
                catch { }
            }

            return "ok";
        }
        #endregion

        #region parsePage
        async Task<bool> parsePage(string cat, int page)
        {
            var cookie = LoadCookie(memoryCache);

            string url = $"{AppInit.conf.TorrentBy.rqHost()}/{cat}/?page={page}";
            string html = await CoreHttp.Get(url,
                useproxy: AppInit.conf.TorrentBy.useproxy,
                cookie: cookie,
                addHeaders: new List<(string name, string val)>
                {
                    ("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"),
                    ("Accept-Language", "ru,en-US;q=0.9,en;q=0.8"),
                    ("Cache-Control", "no-cache"),
                    ("Pragma", "no-cache"),
                    ("Connection", "keep-alive"),
                    ("Upgrade-Insecure-Requests", "1"),
                    ("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36")
                });

            if (html == null)
                return false;

            // If we got login page, try re-login once
            if (html.Contains("name=\"username\"") && html.Contains("name=\"password\""))
            {
                await TakeLogin();
                await Task.Delay(500);

                html = await CoreHttp.Get(url,
                    useproxy: AppInit.conf.TorrentBy.useproxy,
                    cookie: LoadCookie(memoryCache));
                if (html == null)
                    return false;
            }

            var torrents = new List<TorrentBaseDetails>();
            foreach (string row in tParse.ReplaceBadNames(html).Split("<tr class=\"ttable_col").Skip(1))
            {
                if (string.IsNullOrWhiteSpace(row) || !row.Contains("magnet:?xt=urn"))
                    continue;

                string Match(string pattern, int index = 1)
                {
                    var m = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline).Match(row);
                    string res = HtmlDecode(m.Groups.Count > index ? m.Groups[index].Value : string.Empty);
                    res = Regex.Replace(res ?? string.Empty, "[\\n\\r\\t ]+", " ");
                    return res.Trim();
                }

                #region created
                DateTime createTime = default;

                // Сегодня / Вчера с временем
                var mToday = Regex.Match(row, @">Сегодня,?\s*([0-9]{2}):([0-9]{2})<", RegexOptions.IgnoreCase);
                if (mToday.Success && int.TryParse(mToday.Groups[1].Value, out int th) && int.TryParse(mToday.Groups[2].Value, out int tm))
                {
                    createTime = DateTime.Today.AddHours(th).AddMinutes(tm);
                }
                else
                {
                    var mYesterday = Regex.Match(row, @">Вчера,?\s*([0-9]{2}):([0-9]{2})<", RegexOptions.IgnoreCase);
                    if (mYesterday.Success && int.TryParse(mYesterday.Groups[1].Value, out int yh) && int.TryParse(mYesterday.Groups[2].Value, out int ym))
                    {
                        createTime = DateTime.Today.AddDays(-1).AddHours(yh).AddMinutes(ym);
                    }
                }

                // Без времени (просто "Сегодня" / "Вчера")
                if (createTime == default)
                {
                    if (Regex.IsMatch(row, @">\s*Сегодня\s*<", RegexOptions.IgnoreCase))
                        createTime = DateTime.Today;
                    else if (Regex.IsMatch(row, @">\s*Вчера\s*<", RegexOptions.IgnoreCase))
                        createTime = DateTime.Today.AddDays(-1);
                }

                // Явная дата вида 2025-10-03
                if (createTime == default)
                {
                    string _create = Match(@">([0-9]{4}\-[0-9]{2}\-[0-9]{2})<").Replace("-", " ");
                    if (DateTime.TryParseExact(_create, "yyyy MM dd", new CultureInfo("ru-RU"), DateTimeStyles.None, out var dt))
                        createTime = dt;
                }

                if (createTime == default) continue;
                #endregion

                #region link + title (relative/absolute, без привязки к name="search_select")
                string fullUrl = null;

                // абсолютный href на домен torrent.by
                string hrefAbs = Match(@"<a[^>]+href=""(https?:\/\/(?:www\.)?torrent\.by\/[^""]+)""");
                if (!string.IsNullOrEmpty(hrefAbs))
                {
                    fullUrl = hrefAbs;
                }
                else
                {
                    // относительный
                    string pathRel = Match(@"<a[^>]+href=""\/([^""]+)""");
                    if (!string.IsNullOrEmpty(pathRel))
                        fullUrl = $"{AppInit.conf.TorrentBy.host}/{pathRel}";
                }
                if (string.IsNullOrEmpty(fullUrl)) continue;

                // title
                string rawTitle = Match(@"<a[^>]*>([^<]+)</a>");
                string title = StripTags(rawTitle);
                if (string.IsNullOrEmpty(title)) continue;
                #endregion

                #region other data
                // magnet (берём первый)
                string magnet = Match(@"href=""(magnet:[^""]+)""");
                if (string.IsNullOrEmpty(magnet)) continue;
                magnet = WebUtility.UrlDecode(HtmlDecode(magnet));

                // размер — первая ячейка <td>, начинающаяся с текста, а не с тега
                string sizeName = Match(@"</td>\s*<td[^>]*>\s*([^<][^<]*)</td>");

                // сиды/пиры
                string _sid = Match(@"<font[^>]*color=""green""[^>]*>[^0-9]*([0-9]+)</font>");
                string _pir = Match(@"<font[^>]*color=""red""[^>]*>[^0-9]*([0-9]+)</font>");
                int.TryParse(_sid, out int sid);
                int.TryParse(_pir, out int pir);

                // try extract names / year: "Name / Original (2025) ..."
                string name = null, originalname = null;
                int relased = 0;
                var g = Regex.Match(title, @"^\s*(?<ru>[^/]+?)(?:\s*/\s*(?<en>[^/]+?))?\s*\((?<y>\d{4})", RegexOptions.Singleline);
                if (g.Success)
                {
                    name = tParse.ReplaceBadNames(g.Groups["ru"].Value).Trim();
                    originalname = tParse.ReplaceBadNames(g.Groups["en"].Value).Trim();
                    int.TryParse(g.Groups["y"].Value, out relased);
                }
                #endregion

                #region types
                var types = MapTypes(cat);
                #endregion

                torrents.Add(new TorrentBaseDetails()
                {
                    trackerName = "torrentby",
                    types = types,
                    url = fullUrl,
                    title = title,
                    sid = sid,
                    pir = pir,
                    sizeName = sizeName,
                    magnet = magnet,
                    createTime = createTime,
                    name = name,
                    originalname = originalname,
                    relased = relased
                });
            }

            FileDB.AddOrUpdate(torrents);
            return torrents.Count > 0;
        }
        #endregion
    }
}
