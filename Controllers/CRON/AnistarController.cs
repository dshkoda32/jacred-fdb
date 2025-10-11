// Controllers/CRON/AnistarController.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web; // HtmlDecode
using Microsoft.AspNetCore.Mvc;
using JacRed.Engine;
using JacRed.Engine.CORE;
using JacRed.Models.Details;

namespace JacRed.Controllers.CRON
{
    [Route("/cron/anistar/[action]")]
    public class AnistarController : BaseController
    {
        static bool _workParse = false;

        /// <summary>
        /// Без параметров — парсим все страницы; с limit_page — столько страниц.
        /// Категории:
        ///   /anime/  -> types=["anime"]
        ///   /hentai/ -> types=["anime"]
        ///   /dorams/ -> types=["serial"]
        /// </summary>
        async public Task<string> Parse(int limit_page = 0)
        {
            if (_workParse) return "work";
            _workParse = true;

            var sw = Stopwatch.StartNew();

            int pagesTotal = 0, postsTotal = 0, torrentsFound = 0;
            int saved = 0, skipped = 0, failed = 0;

            var pagesLog = new List<string>();

            var categories = new[]
            {
                new { path = "anime",  types = new[] { "anime"  } },
                new { path = "hentai", types = new[] { "anime"  } },
                new { path = "dorams", types = new[] { "serial" } },
            };

            try
            {
                foreach (var cat in categories)
                {
                    int lastPage = await DetectLastPage(cat.path, limit_page);

                    for (int page = 1; page <= lastPage; page++)
                    {
                        string listUrl = page == 1
                            ? $"{AppInit.conf.Anistar.host}/{cat.path}/"
                            : $"{AppInit.conf.Anistar.host}/{cat.path}/page/{page}/";

                        pagesLog.Add(listUrl);
                        pagesTotal++;

                        string listHtml = await HttpClient.Get(listUrl, useproxy: AppInit.conf.Anistar.useproxy);
                        if (listHtml == null)
                            continue;

                        // Посты вида https://anistar.org/12345-some-title.html
                        var postLinks = Regex.Matches(listHtml, @"https:\/\/anistar\.org\/\d{2,}-[^""'>]+?\.html", RegexOptions.IgnoreCase)
                                             .Cast<Match>()
                                             .Select(m => m.Value)
                                             .Distinct()
                                             .ToList();

                        postsTotal += postLinks.Count;

                        var batch = new List<TorrentDetails>();

                        foreach (string postUrl in postLinks)
                        {
                            string postHtml = await HttpClient.Get(postUrl,
                                useproxy: AppInit.conf.Anistar.useproxy,
                                referer: listUrl);

                            if (postHtml == null)
                                continue;

                            // <h1> Рус / Original </h1>
                            string h1 = Regex.Match(postHtml, @"<h1[^>]*>\s*(.*?)\s*</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)
                                             .Groups[1].Value;
                            h1 = HttpUtility.HtmlDecode(Regex.Replace(h1, @"\s+", " ").Trim());

                            string name = h1, originalname = null;
                            if (h1.Contains(" / "))
                            {
                                var parts = h1.Split(new[] { " / " }, 2, StringSplitOptions.None);
                                name = parts[0].Trim();
                                originalname = parts[1].Trim();
                            }
                            name = tParse.ReplaceBadNames(name);
                            if (!string.IsNullOrWhiteSpace(originalname))
                                originalname = tParse.ReplaceBadNames(originalname);

                            // Ищем открывающие теги блоков торрента: <div id="torrent_{id}_info" class="torrent">
                            foreach (Match tm in Regex.Matches(postHtml,
                                       @"<div id=""torrent_(\d+)_info""\s+class=""torrent""",
                                       RegexOptions.IgnoreCase))
                            {
                                string tid = tm.Groups[1].Value;

                                // Берём «окрестность» блока — дальше в ней ищем info_d1, дату, сиды/пиры
                                int startIdx = tm.Index;
                                int endIdx = Math.Min(postHtml.Length, startIdx + 4000);
                                string around = postHtml.Substring(startIdx, endIdx - startIdx);

                                // info_d1: "2 (487.16 Mb)" или "126-129 (2.24 Gb)" и т.п.
                                var mInfo = Regex.Match(around, @"<div class=""info_d1"">\s*([^<]+?)\s*</div>",
                                                        RegexOptions.IgnoreCase | RegexOptions.Singleline);
                                string infoText = mInfo.Success ? HttpUtility.HtmlDecode(mInfo.Groups[1].Value).Trim() : null;

                                // эпизод/диапазон
                                string epLabel;      // "Серия 2" или "Серии 126-129"
                                string epNumForUrl;  // для ?e= в уникальном url (левая граница диапазона)
                                if (!string.IsNullOrEmpty(infoText))
                                {
                                    var range = Regex.Match(infoText, @"\b(\d{1,4})\s*-\s*(\d{1,4})\b");
                                    if (range.Success)
                                    {
                                        string a = range.Groups[1].Value;
                                        string b = range.Groups[2].Value;
                                        epLabel = $"Серии {a}-{b}";
                                        epNumForUrl = a;
                                    }
                                    else
                                    {
                                        var single = Regex.Match(infoText, @"\b(\d{1,4})\b");
                                        epNumForUrl = single.Success ? single.Groups[1].Value : "1";
                                        epLabel = $"Серия {epNumForUrl}";
                                    }
                                }
                                else
                                {
                                    epNumForUrl = "1";
                                    epLabel = "Серия 1";
                                }

                                // дата dd-MM-yyyy
                                DateTime createTime = DateTime.Today;
                                var mDate = Regex.Match(around, @"\b(\d{2})-(\d{2})-(\d{4})\b");
                                if (mDate.Success &&
                                    DateTime.TryParseExact(mDate.Value, "dd-MM-yyyy", CultureInfo.InvariantCulture,
                                                           DateTimeStyles.None, out DateTime ct))
                                {
                                    createTime = ct;
                                }
                                int relased = createTime.Year;

                                // сиды/пиры
                                int sid = 0, pir = 0;
                                var mSid = Regex.Match(around, @"<div class=""li_distribute"">\s*([0-9]+)\s*</div>", RegexOptions.IgnoreCase);
                                if (mSid.Success) int.TryParse(mSid.Groups[1].Value, out sid);
                                var mPir = Regex.Match(around, @"<div class=""li_swing"">\s*([0-9]+)\s*</div>", RegexOptions.IgnoreCase);
                                if (mPir.Success) int.TryParse(mPir.Groups[1].Value, out pir);

                                // уникальный URL «как в Anidub»
                                string uniqueUrl = $"{postUrl}?e={epNumForUrl}&id={tid}";

                                // заголовок: ключ по name/originalname + "Серия N" / "Серии A-B"
                                string titleBase = string.IsNullOrWhiteSpace(originalname) ? name : $"{name} / {originalname}";
                                string title = $"{titleBase} — {epLabel}";

                                batch.Add(new TorrentDetails
                                {
                                    trackerName = "anistar",
                                    types = cat.types,
                                    url = uniqueUrl,
                                    title = title,
                                    sid = sid,
                                    pir = pir,
                                    createTime = createTime,
                                    name = name,
                                    originalname = originalname,
                                    relased = relased
                                });

                                torrentsFound++;
                            }
                        }

                        // Батчевое сохранение с докачкой .torrent и ретраями
                        await FileDB.AddOrUpdate(batch, async (t, db) =>
                        {
                            try
                            {
                                if (db.TryGetValue(t.url, out TorrentDetails cached) && cached.title == t.title)
                                {
                                    skipped++;
                                    return true; // запись актуальна
                                }

                                string id = Regex.Match(t.url ?? "", @"[?&]id=([0-9]+)", RegexOptions.IgnoreCase).Groups[1].Value;
                                if (string.IsNullOrWhiteSpace(id))
                                {
                                    failed++;
                                    return false;
                                }

                                // 5× с паузой 1 мин.
                                for (int i = 0; i < 5; i++)
                                {
                                    byte[] tor = await HttpClient.Download(
                                        $"{AppInit.conf.Anistar.host}/engine/gettorrent.php?id={id}",
                                        referer: AppInit.conf.Anistar.host,
                                        useproxy: AppInit.conf.Anistar.useproxy);

                                    string magnet = BencodeTo.Magnet(tor);
                                    if (!string.IsNullOrWhiteSpace(magnet))
                                    {
                                        t.magnet = magnet;
                                        t.sizeName = BencodeTo.SizeName(tor);
                                        saved++;
                                        return true;
                                    }

                                    await Task.Delay(TimeSpan.FromMinutes(1));
                                }

                                // 5× с паузой 10 мин.
                                for (int i = 0; i < 5; i++)
                                {
                                    byte[] tor = await HttpClient.Download(
                                        $"{AppInit.conf.Anistar.host}/engine/gettorrent.php?id={id}",
                                        referer: AppInit.conf.Anistar.host,
                                        useproxy: AppInit.conf.Anistar.useproxy);

                                    string magnet = BencodeTo.Magnet(tor);
                                    if (!string.IsNullOrWhiteSpace(magnet))
                                    {
                                        t.magnet = magnet;
                                        t.sizeName = BencodeTo.SizeName(tor);
                                        saved++;
                                        return true;
                                    }

                                    await Task.Delay(TimeSpan.FromMinutes(10));
                                }

                                failed++;
                                return false;
                            }
                            catch
                            {
                                failed++;
                                return false;
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _workParse = false;
                return $"anistar parse error: {ex.GetType().Name}: {ex.Message}";
            }

            _workParse = false;
            sw.Stop();

            return string.Join("\n", new[]
            {
                "AniStar parse summary:",
                $"  Pages: {pagesTotal}",
                $"  Posts found: {postsTotal}",
                $"  Torrents found: {torrentsFound}",
                $"  saved: {saved}, skipped: {skipped}, failed: {failed}",
                $"  Time: {sw.Elapsed}",
                "",
                "Pages scanned:",
                string.Join("\n", pagesLog)
            });
        }

        /// <summary>
        /// Определяет число страниц по пагинации .pagenav/.pages (…/page/NNN/).
        /// Если limit_page > 0 — используем его.
        /// </summary>
        async Task<int> DetectLastPage(string cat, int limit_page)
        {
            if (limit_page > 0)
                return limit_page;

            string url = $"{AppInit.conf.Anistar.host}/{cat}/";
            string html = await HttpClient.Get(url, useproxy: AppInit.conf.Anistar.useproxy);
            if (html == null)
                return 1;

            // ссылки вида /{cat}/page/NNN/
            var nums = Regex.Matches(html, $@"/{Regex.Escape(cat)}/page/([0-9]+)/", RegexOptions.IgnoreCase)
                            .Cast<Match>()
                            .Select(m => int.Parse(m.Groups[1].Value))
                            .ToList();

            return nums.Count == 0 ? 1 : nums.Max();
        }
    }
}
