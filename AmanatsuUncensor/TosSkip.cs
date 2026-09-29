using System;
using HarmonyLib;
using Cysharp.Threading.Tasks;
using ResultTuple = Il2CppSystem.ValueTuple<bool, string>;

namespace Amanatsu.Uncensor
{
    /// <summary>
    /// O jogo morre no Title quando o aceite dos termos nao alcanca o servidor:
    ///
    ///   Exception: サーバーへアクセスするための情報の読み込みに失敗しました。
    ///     AL.Dialog.TOS.TermsOfServiceProvider:TryConnectLicenceAgreementAsync(...)
    ///     AL.Title.&lt;Start&gt;d__33:MoveNext()
    ///
    /// MEDIDO, nao repetir o erro: pular TryConnectLicenceAgreement (o metodo do dialogo) mata a
    /// inicializacao do jogo junto. Ele recebe um Func&lt;UniTask&lt;bool&gt;&gt; onLoadAsync - e
    /// DENTRO dele que o jogo carrega os proprios dados, com o dialogo na tela. Sem isso, a carta
    /// de DefaultData passou a responder carregado=False e o cenario da criacao ficou sem render.
    ///
    /// Aqui so a ida ao servidor e trocada: respondemos (true, baseURL), e o resto do fluxo -
    /// inclusive o onLoadAsync - roda igual ao original.
    /// </summary>
    internal static class TosSkip
    {
        // TODO(tos): desvio de desenvolvimento. Antes de qualquer distribuicao, remover e tratar de
        // verdade: deixar a falha de rede nao matar a Start do Title, em vez de fingir que o
        // servidor respondeu.
        internal static void Aplica(Harmony harmony)
        {
            try
            {
                var alvo = AccessTools.Method(
                    typeof(AL.Dialog.TOS.TermsOfServiceProvider),
                    nameof(AL.Dialog.TOS.TermsOfServiceProvider.TryConnectTOSServerAsync));

                if (alvo == null)
                {
                    UncensorPlugin.Logger.LogWarning("[TOS] TryConnectTOSServerAsync not found; nothing skipped.");
                    return;
                }

                harmony.Patch(alvo, prefix: new HarmonyMethod(typeof(TosSkip), nameof(SkipConnect)));
                UncensorPlugin.Logger.Info("[TOS] terms server access skipped (development bypass).");

                // O Player.log nomeou o ponto que estoura de verdade: a funcao local
                // <TryConnectLicenceAgreementAsync>g__CheckVersionAsync|0, que no interop virou
                // este nome gerado. Buscado por string porque o tipo e uma classe de captura.
                var versao = AccessTools.Method(
                    AccessTools.TypeByName("AL.Dialog.TOS.TermsOfServiceProvider+__c__DisplayClass8_0"),
                    "Method_Internal_UniTask_1_Boolean_String_CancellationToken_0");

                if (versao == null)
                {
                    UncensorPlugin.Logger.LogWarning("[TOS] CheckVersionAsync not found; the drop at Title continues.");
                    return;
                }

                harmony.Patch(versao, prefix: new HarmonyMethod(typeof(TosSkip), nameof(SkipVersionCheck)));
                UncensorPlugin.Logger.Info("[TOS] terms version check skipped.");
            }
            catch (Exception ex)
            {
                UncensorPlugin.Logger.LogWarning($"[TOS] could not skip the access: {ex.Message}");
            }
        }

        private static bool SkipConnect(string baseURL, ref UniTask<ResultTuple> __result)
        {
            __result = new UniTask<ResultTuple>(new ResultTuple(true, baseURL ?? string.Empty));
            return false;
        }

        // true = "a versao conferida ja e a aceita", entao o dialogo nao abre. Com false ele
        // abre a cada inicializacao - foi testado, e nao e o que queremos.
        private static bool SkipVersionCheck(ref UniTask<bool> __result)
        {
            __result = new UniTask<bool>(true);
            return false;
        }
    }
}
