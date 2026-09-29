using UnityEngine;
using AmanatsuVR.Logging;
using Flags = Character.HumanData.LoadFileInfo.Flags;
using HumanData = Character.HumanData;
using HumanCustom = CharacterCreation.HumanCustom;

namespace AmanatsuVR.VRUtils
{
    // ========================================================================================
    // O QUE JA ESTA RESOLVIDO - nao repetir estas medicoes:
    //  - A garota branca nao e bug de render: acontece igual no desktop, com luz e mascara
    //    descartadas por medicao.
    //  - Causa medida: a cena de criacao e aberta SEM carta. O [FLUXO] pegou
    //    Received(new=True sex=1 data=nulo) e as tres copias nulas em Initialize e em Start;
    //    o editor entao monta um HumanData zerado, cujas cores nascem em #FFF. Por isso as
    //    paletas aparecem em branco e mexer numa cor conserta so aquela cor.
    //  - HumanData.InitializeData e HumanCustom.LoadChara NAO estao nesse caminho: patch
    //    aplicado nos dois, zero linha no log.
    //  - O ponto que apaga e SetReceiver(byte,bool), chamado depois do Initialize.
    //  - A correcao FUNCIONA no desktop: trocar essa chamada por SetReceiver(carta, deleteAll)
    //    com a carta de DefaultData/0/chara/female/AL_F_00.png (LoadFromPreset responde False
    //    neste build; a carta de disco carrega limpa, erro=None -> cor(0,96 0,83 0,77)).
    //
    // A QUEDA EM VR ERA OUTRA COISA, e nao este arquivo: o jogo morria no Title, antes da
    // CustomScene e sem nenhuma linha [CARTA] no log - ou seja, sem este codigo ter rodado. Era
    // o aceite dos termos falhando ao acessar o servidor e levando a Start do Title junto.
    // Contornado no mod Uncensor (AmanatsuUncensor/TosSkip.cs); o TODO de verdade mora la.
    //
    // E se um dia a carta voltar a responder carregado=False com erro=None: olhe a pasta
    // DefaultData do jogo ANTES de culpar qualquer patch. Ja aconteceu de ela estar vazia (as
    // cartas tinham ido parar so em dist/DefaultData), e o sintoma e identico ao de um bug nosso.
    // ========================================================================================

    /// <summary>
    /// A garota branca nao e problema de VR: acontece igual no desktop, e o [DADO] mostrou que os
    /// DADOS entram em branco (arquivo='' erro=None, todas as cores em #FFF) - nada falhou ao
    /// carregar porque nada foi carregado.
    ///
    /// Duas tentativas de gancho sairam mudas: HumanData.InitializeData e HumanCustom.LoadChara.
    /// Patch aplicado nas duas, zero linha, e o Update da mesma classe funciona - entao nao e o
    /// Harmony, e esses metodos e que nao estao nesse caminho. Adivinhar um terceiro gancho custa
    /// mais uma rodada de teste, entao esta rodada mede o fluxo inteiro de uma vez: cada ponto
    /// candidato so anuncia que passou e em que estado os dados estavam.
    ///
    /// O conserto vai junto no ponto mais cedo que tiver dado: pedir ao jogo o preset que ele ja
    /// traz (Flags.Custom - so aparencia, sem mexer em nome/sexo/parametros), e so se os dados
    /// estiverem mesmo virgens. Carta de verdade aberta, nada roda.
    /// </summary>
    internal static class CharDefaults
    {
        internal static void Anuncia(string onde, HumanCustom hc)
        {
            if (hc == null) { PluginLog.Info($"[AmanatsuVR][FLOW] {onde}: null instance"); return; }

            var r = hc.Received;
            string recebido = r == null
                ? "Received=null"
                : $"Received(new={r.ModeNew} sex={r.ModeSex} deleteAll={r.DeleteAll} data={Estado(r.HumanData)})";

            PluginLog.Info($"[AmanatsuVR][FLOW] {onde}: {recebido}"
                + $" | live={Estado(hc.HumanData)} edit={Estado(hc.EditHumanData)}"
                + $" default={Estado(hc.DefaultData)} human={(hc.Human == null ? "no" : "built")}");
        }

        private static string Estado(HumanData d)
        {
            var c = d?.Custom;
            if (c == null || c.Body == null || c.Face == null) return "null";
            return EstaVirgem(c) ? "BLANK" : $"color({c.Body.skinMainColor.r:0.00},{c.Body.skinMainColor.g:0.00},{c.Body.skinMainColor.b:0.00})";
        }

        /// <summary>
        /// Devolve a carta padrao do jogo para o sexo pedido, ou null se nenhuma fonte responder.
        /// Duas fontes, nessa ordem: o preset que o proprio jogo expoe - que neste build responde
        /// False - e a carta de disco em DefaultData, a mesma que o seletor de personagem mostra.
        /// </summary>
        internal static HumanData CartaPadrao(byte sexo)
        {
            // Um throw daqui subiria para dentro do IL2CPP, que nao tem onde pegar: derruba o jogo.
            try { return CarregaCarta(sexo); }
            catch (System.Exception ex)
            {
                PluginLog.Warning($"[AmanatsuVR][CARD] default card failed entirely: {ex}");
                return null;
            }
        }

        private static HumanData CarregaCarta(byte sexo)
        {
            var d = new HumanData();
            HumanData.InitializeData(d);

            bool ok = false;
            try { ok = d.LoadFromPreset(sexo, Flags.Default, 0); }
            catch (System.Exception ex) { PluginLog.Warning($"[AmanatsuVR][CARD] preset failed: {ex.Message}"); }
            PluginLog.Info($"[AmanatsuVR][CARD] preset 0 (sex={sexo}) loaded={ok} -> {Estado(d)}");

            if (!ok || Estado(d) == "BLANK")
            {
                string caminho = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                    Application.dataPath, "..", "DefaultData", "0", "chara",
                    sexo == 0 ? "male" : "female", sexo == 0 ? "AL_M_00.png" : "AL_F_00.png"));
                try { ok = System.IO.File.Exists(caminho) && d.LoadCharaFile(caminho, Flags.Default); }
                catch (System.Exception ex) { ok = false; PluginLog.Warning($"[AmanatsuVR][CARD] file failed: {ex.Message}"); }
                PluginLog.Info($"[AmanatsuVR][CARD] disk '{caminho}' loaded={ok}"
                    + $" error={d._lastLoadErrorCode} -> {Estado(d)}");
            }

            return (ok && Estado(d) != "BLANK") ? d : null;
        }

        /// <summary>Fix as tres copias; a padrao importa porque e dela que o botao de restaurar puxa.</summary>
        internal static void Fix(string onde, HumanCustom hc)
        {
            if (hc == null) return;
            FixOne(onde, "vivo", hc.HumanData);
            FixOne(onde, "edicao", hc.EditHumanData);
            FixOne(onde, "padrao", hc.DefaultData);
        }

        private static void FixOne(string onde, string rotulo, HumanData d)
        {
            var c = d?.Custom;
            if (c == null || c.Body == null || c.Face == null) return;
            if (!EstaVirgem(c)) return;

            byte sexo = d.Parameter != null ? d.Parameter.sex : (byte)1;
            try
            {
                bool ok = d.LoadFromPreset(sexo, Flags.Custom, 0);
                PluginLog.Info($"[AmanatsuVR][DEFAULT] {onde}/{rotulo}: blank -> preset 0"
                    + $" (sex={sexo}) loaded={ok} skinMain={d.Custom.Body.skinMainColor}");
            }
            catch (System.Exception ex)
            {
                PluginLog.Warning($"[AmanatsuVR][DEFAULT] {onde}/{rotulo}: preset failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Branco puro em quatro cores independentes ao mesmo tempo nao e escolha de ninguem: e o
        /// valor com que o campo nasce. Uma carta de verdade erra pelo menos uma delas.
        /// </summary>
        private static bool EstaVirgem(Character.HumanDataCustom c)
        {
            return c.Body.skinMainColor == Color.white
                && c.Face.eyebrowColor == Color.white
                && c.Face.noseColor == Color.white
                && c.Face.detailColor == Color.white;
        }
    }

    /// <summary>
    /// O [FLUXO] fechou a questao: a cena entra com Received(new=True sex=1 data=nulo) e as tres
    /// copias ainda nulas em Initialize e em Start. Ninguem deixou de carregar - o editor e mandado
    /// abrir SEM carta, e monta um HumanData zerado por conta propria. Dai as paletas em #FFF.
    ///
    /// Como carregar uma carta na mao resolve (testado), a correcao e dar a carta antes: enchemos o
    /// Received com a carta padrao que o jogo ja traz, pelo caminho que ele mesmo usa para "editar
    /// este personagem". Se o Received ja vier com carta, nada aqui roda.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(HumanCustom), "Initialize")]
    internal static class CharInitializePatch
    {
        private static void Prefix(HumanCustom __instance)
        {
            CharDefaults.Anuncia("Initialize:antes", __instance);
        }

        private static void Postfix(HumanCustom __instance)
        {
            CharDefaults.Anuncia("Initialize:depois", __instance);
            CharDefaults.Fix("Initialize", __instance);
        }
    }

    [HarmonyLib.HarmonyPatch(typeof(HumanCustom), "Start")]
    internal static class CharStartPatch
    {
        private static void Prefix(HumanCustom __instance) => CharDefaults.Anuncia("Start:antes", __instance);
        private static void Postfix(HumanCustom __instance) => CharDefaults.Anuncia("Start:depois", __instance);
    }

    [HarmonyLib.HarmonyPatch(typeof(HumanCustom), "LoadChara")]
    internal static class CharLoadCharaPatch
    {
        private static void Prefix(HumanCustom __instance)
        {
            CharDefaults.Anuncia("LoadChara:antes", __instance);
            CharDefaults.Fix("LoadChara", __instance);
        }
    }

    /// <summary>
    /// Este e o bug, medido: a carta entregue no Initialize chegava inteira
    /// (Received data=cor(0,96 0,83 0,77)) e logo depois o jogo chamava SetReceiver(1, true),
    /// que devolve tudo para novo=True data=nulo - a cena e mandada abrir sem carta, e o editor
    /// monta um HumanData zerado. Dai as paletas em #FFF.
    ///
    /// Entao trocamos a propria chamada: em vez de "abra vazio para o sexo X", passamos "edite
    /// esta carta", com a carta padrao que o jogo ja traz - o mesmo efeito de carregar na mao, que
    /// o Marcus confirmou que resolve. Sem carta disponivel, deixamos o original seguir: pior do
    /// que hoje nao fica.
    /// </summary>
    // a queda no Title em VR.
    [HarmonyLib.HarmonyPatch(typeof(HumanCustom), nameof(HumanCustom.SetReceiver), typeof(byte), typeof(bool))]
    internal static class CharSetReceiverPatch
    {
        private static bool Prefix(HumanCustom __instance, byte sex, bool deleteAll)
        {
            if (__instance == null) return true;

            var carta = CharDefaults.CartaPadrao(sex);
            if (carta == null)
            {
                PluginLog.Info($"[AmanatsuVR][CARD] no card for sex={sex}; letting the original open empty");
                return true;
            }

            __instance.SetReceiver(carta, deleteAll);
            PluginLog.Info($"[AmanatsuVR][CARD] SetReceiver(sex={sex}) replaced by the default card");
            return false;
        }
    }
}
