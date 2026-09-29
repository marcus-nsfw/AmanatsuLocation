using AmanatsuVR.Config;
using AmanatsuVR.Logging;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace AmanatsuVR.VRUtils
{
    /// <summary>
    /// Conserta o culling da segunda passada estereo.
    ///
    /// O que foi medido, em ordem:
    ///
    /// 1. RenderDoc, quadro 467: a passada opaca do olho esquerdo emite 50 draws indexados e
    ///    309 instanciados; a do olho direito emite 3 e nenhum instanciado, e esses 3 sao
    ///    exatamente os 3 primeiros do esquerdo. A geometria nunca chega a ser submetida a
    ///    GPU, entao nao era depth, blend nem layout de textura.
    ///
    /// 2. Log [XRCULL]: em XR a URP nao usa o culling da camera, usa o que o XRDisplaySubsystem
    ///    devolve por cullingPassIndex. O indice 0 vem correto (fovY 102, far 664, planos
    ///    obliquos). O indice 1 vem como uma caixa alinhada aos eixos de 2x2x2 metros centrada
    ///    na cabeca, fovY 90, far infinito - praticamente nao inicializado. Sobrevive so o que
    ///    encosta nesse cubo: o ceu e os nossos objetos a 1,2 m. Bate com os 3 draws.
    ///
    /// 3. Pedir SinglePassInstanced nao adianta: o loader le o pedido e o nativo continua em
    ///    MultiPass, com passes=2 e o indice 1 igualmente vazio.
    ///
    /// Duas tentativas de enxerto por Harmony fracassaram, e vale registrar por que: postfix em
    /// UniversalRenderPipeline.TryGetCullingParameters nunca roda (metodo privado curto, o
    /// il2cpp inlina); prefixo em ScriptableRenderContext.Cull deixa os dois olhos pretos mesmo
    /// sendo um prefixo vazio, sem parametro nenhum - patchar aquele metodo ja quebra por si so.
    ///
    /// Este caminho nao usa Harmony. XRSystem.SetLayoutOverride e a API publica que o proprio
    /// Unity oferece para montar o layout estereo: chamamos CreateDefaultLayout para obter o
    /// layout normal e depois corrigimos os passes ruins. O setter de XRPass.cullingParams
    /// recebe a struct por valor, o que evita de vez o marshalling por referencia.
    ///
    /// Os olhos distam ~6,5 cm: usar o frustum do olho bom no outro perde uma fatia fina na
    /// borda, invisivel na pratica e infinitamente melhor que nao desenhar nada.
    /// </summary>
    public static class XRCullingFix
    {
        private static Il2CppSystem.Action<XRLayout, Camera> _override;
        private static int _consertos;
        private static bool _jaLogouLayout;


        public static void Install()
        {
            if (!PluginConfig.FixStereoCulling.Value || _override != null) return;

            try
            {
                _override = (Il2CppSystem.Action<XRLayout, Camera>)(
                    (System.Action<XRLayout, Camera>)MontaLayout);
                XRSystem.SetLayoutOverride(_override);
                PluginLog.Info("[AmanatsuVR][XRCULL] layout override instalado.");
            }
            catch (System.Exception ex)
            {
                _override = null;
                PluginLog.Warning($"[AmanatsuVR][XRCULL] SetLayoutOverride falhou: {ex.Message}");
            }
        }

        /// <summary>
        /// Um frustum em perspectiva nunca tem os seis planos alinhados aos eixos. A caixa que
        /// o plugin OpenVR devolve tem. E o teste mais direto que a medicao permite.
        /// </summary>
        private static bool Degenerado(ScriptableCullingParameters cp)
        {
            if (cp.isOrthographic || cp.cullingPlaneCount < 6) return false;

            for (int i = 0; i < 6; i++)
            {
                var n = cp.GetCullingPlane(i).normal;
                bool axial = Mathf.Abs(n.x) > 0.999f
                          || Mathf.Abs(n.y) > 0.999f
                          || Mathf.Abs(n.z) > 0.999f;
                if (!axial) return false;
            }
            return true;
        }

        /// <summary>
        /// Rastro da primeira montagem de layout. O crash anterior foi nativo: nenhuma excecao
        /// gerenciada, o log simplesmente para. PluginLog escreve em arquivo com flush a cada
        /// linha, entao a ultima linha gravada identifica a chamada que derrubou o processo.
        /// Sai de cena depois da primeira passagem inteira.
        /// </summary>
        private static void Passo(string onde)
        {
            if (!_jaLogouLayout) PluginLog.Info($"[AmanatsuVR][XRCULL] passo: {onde}");
        }

        private static void MontaLayout(XRLayout layout, Camera camera)
        {
            // Nao chamamos CreateDefaultLayout: o Unity ja montou o layout antes de nos chamar.
            // Medido: chamando, o log mostrava multipassId=0,1,2,3 - quatro passadas para dois
            // olhos, ou seja, tudo adicionado duas vezes. Isso disparava
            // "Collection was modified" na enumeracao interna e o olho direito congelava porque
            // a segunda passada nunca chegava a ser submetida.
            try
            {
                var passes = layout.GetActivePasses();
                Passo($"GetActivePasses ok, nulo={passes == null}");
                if (passes == null) { _jaLogouLayout = true; return; }

                int total = passes.Count;
                Passo($"Count={total}");
                if (total < 2) { _jaLogouLayout = true; return; }

                ScriptableCullingParameters bom = default;
                bool temBom = false;

                for (int i = 0; i < total; i++)
                {
                    Passo($"lendo tupla {i}");
                    var pass = passes[i].Item2;
                    Passo($"tupla {i} -> XRPass nulo={pass == null}");
                    if (pass == null) continue;

                    var cp = pass.cullingParams;
                    Passo($"cullingParams {i} lido, planos={cp.cullingPlaneCount}");
                    if (!Degenerado(cp)) { bom = cp; temBom = true; }
                }

                Passo($"varredura terminada, temBom={temBom}");
                if (!temBom) { _jaLogouLayout = true; return; }

                for (int i = 0; i < total; i++)
                {
                    var pass = passes[i].Item2;
                    if (pass == null || !Degenerado(pass.cullingParams)) continue;

                    pass.cullingParams = bom;

                    // So gasta orcamento de log quando houve conserto de verdade: na tela de
                    // titulo nada e degenerado e as linhas se esgotavam antes de chegar no mapa.
                    if (_consertos++ < 8)
                        PluginLog.Info($"[AmanatsuVR][XRCULL] pass {i} cullingPassId={pass.cullingPassId}"
                            + $" multipassId={pass.multipassId} degenerado, substituido"
                            + $" (mask 0x{pass.cullingParams.cullingMask:X8},"
                            + $" planos={pass.cullingParams.cullingPlaneCount})");
                }

                Passo("fim, sem crash");
                _jaLogouLayout = true;
            }
            catch (System.Exception ex)
            {
                _jaLogouLayout = true;
                PluginLog.Warning($"[AmanatsuVR][XRCULL] correcao falhou: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
