using System;
using System.Collections.Generic;
using System.IO;
using Lvn;
using NUnit.Framework;
using UnityEngine;

namespace Lvn.Tests
{
    /// <summary>
    /// ГЛАВУ ПРОХОДИТ САМ ДВИЖОК, А НЕ ТОЛЬКО БРАУЗЕРНЫЙ ПОРТ.
    ///
    /// <para>Стенд `qa/playthrough-check.sh` играет главы плеером песочницы —
    /// быстро и без Unity, но плеер этот ОБЪЯВЛЕННОЕ ПОДМНОЖЕСТВО: операции
    /// вроде гардероба он не толкует и пролетает насквозь. Когда такая глава не
    /// доигрывается, вопрос «кто виноват — глава или порт» остаётся без ответа,
    /// а ответ даёт только эталон.</para>
    ///
    /// <para>Здесь эталон и играет: настоящий <see cref="LvnPlayer"/>, та же
    /// стратегия (помнить, что уже пробовал в этой точке), тот же потолок по
    /// размеру главы. Путь к файлу приходит снаружи —
    /// <c>LVN_PLAYTHROUGH_LVN</c>: партнёрский контент в репозитории не живёт, а
    /// проверка нужна именно на нём.</para>
    /// </summary>
    public class EnginePlaythroughTests
    {
        private sealed class Автомат : ILvnStage
        {
            public int Реплик;
            public IReadOnlyList<LvnOption> Выбор;
            public bool Кончилось;
            public void ShowSay(string who, string text, string style) { Реплик++; Выбор = null; }
            public void ShowChoice(IReadOnlyList<LvnOption> options) => Выбор = options;
            public void ApplyStage(Newtonsoft.Json.Linq.JObject command, Lvn.LvnSender sender) { }
            public void ApplyStage(Newtonsoft.Json.Linq.JObject command) { }
            public void OnEnd() => Кончилось = true;
        }

        [Test]
        public void ГлаваИзвнеДоигрываетсяДвижком()
        {
            var path = Environment.GetEnvironmentVariable("LVN_PLAYTHROUGH_LVN");
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                Assert.Ignore("LVN_PLAYTHROUGH_LVN не задан — проверка нужна на контенте, которого в репозитории нет");

            var doc = LvnDocument.Parse(File.ReadAllText(path));
            int команд = doc.Script?.Count ?? 0;
            int потолок = Mathf.Max(20000, команд * 6);

            for (int seed = 1; seed <= 12; seed++)
            {
                var stage = new Автомат();
                var player = new LvnPlayer(doc, stage);
                var rnd = new System.Random(seed * 7919);
                var пробовали = new Dictionary<string, HashSet<int>>();
                int шагов = 0;

                while (шагов++ < потолок && !player.Finished)
                {
                    if (player.AtChoice && stage.Выбор != null && stage.Выбор.Count > 0)
                    {
                        var точка = player.Index + ":" + stage.Выбор.Count;
                        if (!пробовали.TryGetValue(точка, out var было))
                            пробовали[точка] = было = new HashSet<int>();
                        var свежие = new List<LvnOption>();
                        foreach (var o in stage.Выбор)
                            if (!было.Contains(o.Index)) свежие.Add(o);
                        var пул = свежие.Count > 0 ? свежие : new List<LvnOption>(stage.Выбор);
                        var выбор = пул[rnd.Next(пул.Count)];
                        было.Add(выбор.Index);
                        player.Choose(выбор.Index);
                    }
                    else
                    {
                        player.Advance();
                    }
                }

                if (player.Finished || stage.Кончилось)
                {
                    TestContext.WriteLine($"движок дошёл до конца: заход {seed}, шагов {шагов}, "
                                        + $"реплик {stage.Реплик}, команд в главе {команд}");
                    Assert.Pass();
                }
            }

            Assert.Fail($"движок НЕ доиграл главу ({команд} команд) за 12 заходов по {потолок} шагов — "
                      + "если и браузерный плеер её не проходит, тупик в самой главе");
        }
    }
}
