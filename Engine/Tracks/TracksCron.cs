﻿using MonoTorrent;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace JacRed.Engine
{
    public static class TracksCron
    {
        /// <param name="typetask">
        /// 1 - день
        /// 2 - месяц
        /// 3 - год
        /// 4 - остальное
        /// </param>
        async public static Task Run(int typetask)
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMinutes(typetask == 1 ? 60 : 180));
                if (AppInit.conf.tracks == false)
                    continue;

                if (AppInit.conf.tracksmod == 1 && (typetask == 3 || typetask == 4))
                    continue;

                try
                {
                    var starttime = DateTime.Now;
                    var torrents = new List<(string trackerName, string magnet)>();

                    foreach (var item in FileDB.masterDb.ToArray())
                    {
                        foreach (var t in FileDB.OpenRead(item.Key).Values)
                        {
                            if (string.IsNullOrEmpty(t.magnet))
                                continue;

                            bool isok = false;

                            switch (typetask)
                            {
                                case 1:
                                    isok = t.createTime >= DateTime.UtcNow.AddDays(-1);
                                    break;
                                case 2:
                                    {
                                        if (t.createTime >= DateTime.UtcNow.AddDays(-1))
                                            break;

                                        isok = t.createTime >= DateTime.UtcNow.AddMonths(-1);
                                        break;
                                    }
                                case 3:
                                    {
                                        if (t.createTime >= DateTime.UtcNow.AddMonths(-1))
                                            break;

                                        isok = t.createTime >= DateTime.UtcNow.AddYears(-1);
                                        break;
                                    }
                                case 4:
                                    {
                                        if (t.createTime >= DateTime.UtcNow.AddYears(-1))
                                            break;

                                        isok = true;
                                        break;
                                    }
                                default:
                                    break;
                            }

                            if (isok)
                            {
                                try
                                {
                                    if (TracksDB.theBad(t.types))
                                        continue;

                                    //var magnetLink = MagnetLink.Parse(t.magnet);
                                    //string hex = magnetLink.InfoHash.ToHex();
                                    //if (hex == null)
                                    //    continue;

                                    torrents.Add((t.trackerName, t.magnet));
                                }
                                catch { }
                            }
                        }
                    }

                    foreach (var t in torrents)
                    {
                        try
                        {
                            if (typetask == 2 && DateTime.Now > starttime.AddDays(10))
                                break;

                            if ((typetask == 3 || typetask == 4) && DateTime.Now > starttime.AddMonths(2))
                                break;

                            if (TracksDB.Get(t.magnet) == null)
                            {
                                _ = TracksDB.Add(t.magnet);
                                await Task.Delay(AppInit.conf.tracksdelay);
                            }
                        }
                        catch { }
                    }
                }
                catch { }
            }
        }
    }
}