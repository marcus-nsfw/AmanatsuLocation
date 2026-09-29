using System;
using System.Collections.Generic;
using System.Reflection;
using Character;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using ButtonIndex = AL.Title.TitleScene.ButtonIndex;

namespace Amanatsu.Uncensor
{
    /// <summary>
    /// Freemode: a Recollection serve de menu (local na esquerda, carta na direita). No "play", em
    /// vez do clipe, montamos a H nos mesmos: mapa do local + heroina da carta + HScene direto.
    ///
    /// MEDIDO (decompilado, MovieManager.PlayShortMove): sem save nenhum, o jogo monta a cena assim -
    ///   periodo  = toggle manha/tarde/noite -> 1/2/3
    ///   mapa     = ContentProvider.ShortMovieInfoContents[CurrentMemory.Id].MapID
    ///   _canvas desligado (UI da Recollection some)
    ///   heroina  = Actor.Rent&lt;Heroine&gt;(new NPCData{HumanData=_human, CharaFileName, DataID, UserID}, 1, AccessorySlotNum)
    ///   MapManager.ChangeMapAsync(mapa, periodo, false, null, true, false)
    /// Copiamos isso e, no lugar de ShortMovie.Load/Play, chamamos HScene.InitializeAsync como o
    /// HDetail faz (Humans = [Player, parceira, ...]).
    ///
    /// MEDIDO: Nullable&lt;T&gt; e CancellationToken sao CLASSES no interop - precisam de "new"
    /// explicito, null/default faz Il2CppObjectBaseToPtrNotNull estourar.
    /// MEDIDO: campo privado vira PROPRIEDADE no interop - AccessTools.Field devolve null; usar direto.
    /// MEDIDO: MemoryItem.SetData bloqueia local via HashSet e ClickMemory checa AlreadyRead - forcar os dois.
    /// MEDIDO: o botao clonado do "Memory" mantinha o listener persistente original
    /// (RemoveAllListeners nao limpa no IL2CPP interop) - fix: trocar o campo onClick inteiro.
    /// </summary>
    internal static class Freemode
    {
        private const string NomeBotao = "FreemodeButton";

        // O IL2CPP nao segura delegate criado do lado gerenciado: sem manter a referencia viva o
        // GC leva o callback e o clique vira no-op.
        private static readonly List<Action> _vivos = new List<Action>();

        private static readonly FieldInfo _campoOnClick =
            typeof(Button).GetField("m_OnClick", BindingFlags.NonPublic | BindingFlags.Instance);
        private static bool _emRecollection;
        private static bool _inFreemodeH;

        // MEASURED (IDA): HScene.Resource.GetPostureInfo and FindPosture's Check drop a pose when
        // AnimationListInfo.CheckReleasePhase(favorability, inclusiveness, proactivity, curiosity) fails - the
        // heroine's progress gate. The freemode heroine is rented fresh (all 0), so only the first poses passed.
        // The place filter (PlaceCtrl.Contains) and the weakness split are separate checks and stay.
        private static void UnlockAllPostures(ref bool __result)
        {
            if (_inFreemodeH) __result = true;
        }

        internal static void Aplica(Harmony harmony)
        {
            try
            {
                harmony.Patch(
                    AccessTools.Method(typeof(AL.Title.TitleScene), "Awake"),
                    postfix: new HarmonyMethod(typeof(Freemode), nameof(DepoisDoAwake)));

                harmony.Patch(
                    AccessTools.Method(typeof(AL.Memory.MovieManager), "PlayShortMove"),
                    prefix: new HarmonyMethod(typeof(Freemode), nameof(NoLugarDoClipe)));

                harmony.Patch(
                    AccessTools.Method(typeof(AL.Memory.MemoryItem), "SetData"),
                    postfix: new HarmonyMethod(typeof(Freemode), nameof(ForcaLocalSelecionavel)));

                harmony.Patch(
                    AccessTools.Method(typeof(AL.H.List.AnimationListInfo), nameof(AL.H.List.AnimationListInfo.CheckReleasePhase)),
                    postfix: new HarmonyMethod(typeof(Freemode), nameof(UnlockAllPostures)));

                UncensorPlugin.Logger.Info("[FREE] hooks applied.");
            }
            catch (Exception ex)
            {
                UncensorPlugin.Logger.LogWarning($"[FREE] could not hook: {ex.Message}");
            }
        }

        private static void DepoisDoAwake(AL.Title.TitleScene __instance)
        {
            _emRecollection = false;

            try
            {
                var modelo = __instance.GetMenuButton(ButtonIndex.Memory);
                if (modelo == null)
                {
                    UncensorPlugin.Logger.LogWarning("[FREE] template button not found; nothing injected.");
                    return;
                }

                var pai = modelo.transform.parent;
                if (pai.Find(NomeBotao) != null) return;   // ja injetado nesta cena

                // Clone do botao que o titulo ja tem: herda fonte, sprite, som e escala.
                var go = UnityEngine.Object.Instantiate(modelo.gameObject, pai);
                go.name = NomeBotao;
                go.SetActive(true);
                go.transform.SetSiblingIndex(modelo.transform.GetSiblingIndex() + 1);

                var texto = go.GetComponentInChildren<TMPro.TMP_Text>(true);
                if (texto != null) texto.text = "Freemode";

                var botao = go.GetComponent<Button>();
                if (_campoOnClick != null)
                    _campoOnClick.SetValue(botao, new Button.ButtonClickedEvent());
                else
                    botao.onClick.RemoveAllListeners();

                Action cb = Enter;
                _vivos.Add(cb);
                botao.onClick.AddListener(cb);

                UncensorPlugin.Logger.Info("[FREE] Freemode button injected into the title menu.");
            }
            catch (Exception ex) { UncensorPlugin.Logger.LogWarning($"[FREE] injection failed: {ex}"); }
        }

        private static void Enter()
        {
            try
            {
                _emRecollection = true;
                AL.Title.TitleScene.GotoMemoryScene(false);
                UncensorPlugin.Logger.Info("[FREE] opening Recollection as the pick menu.");
            }
            catch (Exception ex)
            {
                _emRecollection = false;
                UncensorPlugin.Logger.LogWarning($"[FREE] could not open Recollection: {ex}");
            }
        }

        private static bool NoLugarDoClipe(AL.Memory.MovieManager __instance)
        {
            if (!_emRecollection) return true;
            if (__instance._human == null || __instance.CurrentMemory == null)
            {
                UncensorPlugin.Logger.LogWarning("[FREE] pick a location and heroine before play.");
                return false;
            }
            BuildHScene(__instance);
            return false;   // nunca deixa o clipe rodar
        }

        private static async void BuildHScene(AL.Memory.MovieManager mm)
        {
            try
            {
                int periodo = mm._toggleMorning.isOn ? 1 : mm._togglEvening.isOn ? 2 : mm._togglNight.isOn ? 3 : 1;
                int mapa = AL.ContentProvider.ShortMovieInfoContents[mm.CurrentMemory.Id].MapID;
                var carta = mm._human;
                UncensorPlugin.Logger.Info($"[FREE] location {mm.CurrentMemory.Id} -> map {mapa}, period {periodo}, card {carta.CharaFileName}");

                mm._canvas.gameObject.SetActive(false);

                var npc = new AL.User.NPCData();
                npc.HumanData = carta;
                npc.CharaFileName = carta.CharaFileName;
                npc.DataID = carta.About.dataID;
                npc.UserID = carta.About.userID;
                var heroina = await AL.Actor.Rent<AL.Heroine>(npc, 1, Manager.Game.AccessorySlotNum, "", false);
                UncensorPlugin.Logger.Info("[FREE] heroine created.");

                await Manager.MapManager.Instance.ChangeMapAsync(mapa, periodo, false, null, true, false);
                UncensorPlugin.Logger.Info("[FREE] map loaded.");

                // Fora do modo historia nao existe Player: aluga um com carta masculina aleatoria,
                // pelo mesmo Actor.Rent (sex 0 = masculino).
                var jogadorHuman = Manager.Game.Instance?.Player?.Human;
                AL.Player rentedPlayer = null;
                if (jogadorHuman == null)
                {
                    var cartaM = new HumanData();
                    var arquivoM = CartaMasculinaAleatoria();
                    cartaM.LoadCharaFile(arquivoM);
                    var pd = new AL.User.PlayerData();
                    pd.HumanData = cartaM;
                    pd.CharaFileName = cartaM.CharaFileName;
                    pd.DataID = cartaM.About.dataID;
                    pd.UserID = cartaM.About.userID;
                    rentedPlayer = await AL.Actor.Rent<AL.Player>(pd, 0, Manager.Game.AccessorySlotNum, "", false);
                    jogadorHuman = rentedPlayer.Human;
                    UncensorPlugin.Logger.Info($"[FREE] player created with {System.IO.Path.GetFileName(arquivoM)}.");
                }

                // Igual ao HDetail: [player, parceira, terceiro-ou-null]. Repetir a heroina no 3o slot
                // (slot do 3P) fazia a HScene tratar ela como extra e esconder.
                var humanos = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Human>(
                    new Human[] { jogadorHuman, heroina.Human, null });
                var camera = Camera.main
                    ?? UnityEngine.Object.FindObjectOfType<Camera>()
                    ?? new GameObject("FreemodeCamera").AddComponent<Camera>();

                await AL.H.HScene.Create();
                var parametro = new AL.H.Parameter(
                    AL.H.Parameter.HState.Normal,
                    AL.H.Parameter.HPattern.Normal,
                    new Il2CppSystem.Nullable<AL.H.Define.PostureCategory>(),
                    AL.H.PlaceType.Floor,
                    camera,
                    humanos);
                UncensorPlugin.Logger.Info("[FREE] starting HScene.");
                // InitializeAsync so retorna quando o jogador encerra a H.
                _inFreemodeH = true;
                try { await AL.H.HScene.InitializeAsync(parametro, new Il2CppSystem.Threading.CancellationToken()); }
                finally { _inFreemodeH = false; }
                UncensorPlugin.Logger.Info("[FREE] H ended; returning to Recollection.");

                await Cleanup(mm, heroina, rentedPlayer);
            }
            catch (Exception ex) { UncensorPlugin.Logger.LogWarning($"[FREE] failed building the H: {ex}"); }
        }

        // MEDIDO (decompilado, final do PlayShortMove): a limpeza que o jogo faz depois do clipe pra
        // voltar pra tela da Recollection. Sem isso a tela fica parada no fim da H.
        private static async System.Threading.Tasks.Task Cleanup(AL.Memory.MovieManager mm, AL.Heroine heroina, AL.Player rentedPlayer)
        {
            await Manager.Game.SceneFade(FadeCanvas.Fade.In, false, true);
            heroina.Dispose();
            AL.Actor.Return(heroina);
            if (rentedPlayer != null)
            {
                rentedPlayer.Dispose();
                AL.Actor.Return(rentedPlayer);
            }
            Manager.Game.ClearChaGrid();
            Manager.Sound.Stop(Manager.Sound.Type.ENV);
            mm._canvas.gameObject.SetActive(true);
            await Manager.Game.SceneFade(FadeCanvas.Fade.Out, false, true);
            UncensorPlugin.Logger.Info("[FREE] back in Recollection.");
        }

        private static string CartaMasculinaAleatoria()
        {
            var raiz = System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, ".."));
            var cartas = new List<string>();
            foreach (var pasta in new[] { "DefaultData/0/chara/male", "UserData/chara/male" })
            {
                var dir = System.IO.Path.Combine(raiz, pasta);
                if (System.IO.Directory.Exists(dir)) cartas.AddRange(System.IO.Directory.GetFiles(dir, "*.png"));
            }
            return cartas[new System.Random().Next(cartas.Count)];
        }

        private static void ForcaLocalSelecionavel(AL.Memory.MemoryItem __instance)
        {
            try
            {
                // ClickMemory retorna sem fazer nada se AlreadyRead for falso, mesmo com o
                // GameObject Selectable ativo - precisa dos dois.
                __instance.AlreadyRead = true;
                if (__instance.Selectable != null) __instance.Selectable.SetActive(true);
            }
            catch (Exception ex) { UncensorPlugin.Logger.LogWarning($"[FREE] error unlocking location: {ex}"); }
        }
    }
}
