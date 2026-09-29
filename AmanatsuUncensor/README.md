# AmanatsuUncensor

Plugin BepInEx 6 (IL2CPP) com três partes:

| arquivo | o que faz |
| :--- | :--- |
| `Class1.cs` | remove o mosaico em tempo real (`Human.LateUpdate`) |
| `TosSkip.cs` | pula a checagem de termos de serviço |
| `Freemode.cs` | botão **Freemode** no título: escolhe local e heroína na Recollection e inicia uma H jogável |
| `Genitais.cs` | troca os genitais de jogo censurado pelas malhas 3D do BetterPenetration (KKS) |

Build: `dotnet build -c Release` e copiar `bin/Release/net6.0/AmanatsuUncensor.dll` para
`BepInEx/plugins/AmanatsuUncensor/`.

---

## Genitais 3D

O jogo vem com genitais de versão censurada (medido nos bundles com UnityPy):

- **Masculino:** `o_dankon` é uma cápsula lisa de 257 vértices com 2 bones (`cf_j_dan101_00`
  base, `cf_j_dan109_00` ponta, peso linear em z). `o_dan_f` é uma base simples.
- **Feminino:** a virilha das malhas `o_lower_type01..05`/`o_onepi_type01..04` é pele fechada.
  `mnpb` é só um decalque por cima (shader `AL/sub/mnpb`, onde entra o mosaico) e `mnpa` é o ânus.
- A hierarquia fica em `cf_o_root/n_body*/n_dankon` e `n_mnpb`. O `RefObjKey.ObjBody` é só o
  `o_body`, então a busca sobe até o `cf_o_root`.

A técnica vem do UncensorSelector (KK_Plugins): trocar o `sharedMesh` e religar os bones por nome.
Os bundles do KKS são Unity 2019.4 e não carregam no Unity 6, então as malhas são convertidas offline
em `genitais/*.bin` e o plugin monta a `Mesh`, reaproveitando os bindposes da malha original.

| script | o que gera |
| :--- | :--- |
| `genitais/converte_kk.py` | `o_dankon.bin` (SoS, 4353 v) e `o_dan_f.bin` |
| `genitais/converte_kk_fem.py` | `o_lower_type0*.bin` e `o_onepi_type0*.bin` (corpo com a vulva costurada) |

Como o conversor funciona:

- **Escala:** o KK está 10x menor, com os mesmos eixos e os mesmos nomes de bone (`cm_J_` → `cf_j_`).
- **Masculino:** a malha é alinhada pelo bindpose do bone base e escalada pelo vão entre os bones.
- **Feminino:**
  1. recorta a região sob o `o_mnpb` do corpo `SAC Innie`, com teto de altura (sem ele entrava o
     pescoço do KK);
  2. alinha a peça pelos centros dos dois `mnpb`, escalando pela largura deles;
  3. abre o buraco no formato da peça: triângulos cujo centro cai dentro do contorno dela visto
     de baixo, com o contorno em **105%**. Com 90%, o encaixe comprimia a borda e dobrava triângulos
     inclinados; esticar um pouco pra fora não dobra;
  4. limpa as pinças, triângulos que se tocam só por um vértice, até a borda virar um laço simples;
  5. encaixa a borda da peça na borda do buraco:
     - pareia as bordas **por trechos entre 4 âncoras** presentes nas duas (frente, trás, esquerda e
       direita). Dentro de cada trecho usa o comprimento de arco da borda do KK suavizada. Um arco
       global a partir de um ponto só escorregava onde uma borda é mais serrilhada, girava a peça e
       fazia uma metade esticar mais que a outra;
     - deforma o interior de forma **harmônica** (laplaciano com a borda presa);
     - se sobrar triângulo de borda dobrado, descasca e refaz, em até 4 rodadas;
     - também remove "orelhas" do buraco: triângulos de pele com 2 arestas na borda.
  5b. **não usa faixa de costura.** Os pontos da borda da peça viram vértices compartilhados: os
     triângulos de pele encostados no buraco são subdivididos em leque pra incluí-los, e os cantos
     entre pontos consecutivos são fechados com leques pequenos. Um ponto a menos de 0,002 de um canto
     é soldado nele. A faixa antiga ligava pontos no meio dos segmentos da pele e, como o skinning não
     é linear ao longo do segmento, abria filetes brancos na pose.

     **Armadilha:** o `alinha_lacos` pode inverter e girar a borda do KK. Tudo que usa `seg`/`frac`
     precisa usar a borda nessa ordem alinhada. `zip` com a ordem original casava cada ponto com a
     fração de outro.

     Resultado nas 9 variantes: as mesmas bordas abertas da malha original, nenhuma aresta com 3
     triângulos, 1 dobra de área 1e-6.
  6. interpola pesos, cores, tangentes e UV1 a UV3 de forma suave. Na borda, mistura os 2 vértices do
     segmento do buraco onde encostou; no interior, faz uma média da borda ponderada por 1/d².
     Copiar do vértice mais próximo deixava os pesos em degraus, e na pose a peça rasgava e
     descolava da pele. Na borda, a normal é a da pele, misturando com a da peça até `RAIO`.

  **Como conferir a deformação:** `genitais/confere_pose.py` abre e levanta as pernas (os bones de
  cada perna giram juntos em torno do quadril) e compara a peça com a **pele original** na mesma pose.
  O resultado foi: pele estica 9x e vira 134 triângulos; a peça estica 2 a 3x e vira 2 a 5.
  Rotação aleatória em cada bone não serve de teste, porque rasga até a pele original (58x).
  Sempre compare com esse controle.
  6a. A projeção é feita na pele **original inteira** da região, e não só na parte removida. Projetando
     só na removida, um vértice interno perto da borda passava um pouco além dela e grudava no canto do
     buraco, com 100% do peso da coxa. Com as pernas abertas, ele seguia a coxa e fazia uma "ponta" na
     junta da virilha. O `confere_pose_esqueleto.py` mediu esse descolamento: caiu de 0,051 pra 0,005.
     **Esse é o teste de pose que vale:** ele monta o esqueleto real do bundle (hierarquia + TRS local),
     calcula o skin como `mundo_do_bone @ bindpose` e gira as juntas da coxa. Os ajudantes
     `hipleg`/`siri`, que o jogo move por script, giram metade. Na pose de repouso, ele reproduz a
     malha com erro de 0,002.
  6b. **Pesos também projetados na pele removida.** A média da borda por 1/d² diferia do peso da pele
     original no mesmo ponto (mediana 0,24 e máximo 0,87 em L1). Num rig real, com rotações
     encadeadas, isso soltava triângulos no ar com as pernas abertas. Projetado, a diferença cai pra
     mediana 0. A pose simplificada de `confere_pose.py` **não** reproduz esse defeito; use
     `confere_pesos.py`.
  6c. Fica só o **maior pedaço conectado** da peça, a cada rodada. O recorte e a limpeza de pinças
     deixavam 2 triângulos soltos, sem ligação com nada. Pra conferir, use `confere_ilhas.py`.
  7. UVs (0 a 3) e cor são **projetados na pele removida**: cada vértice herda o que a pele original
     tinha no ponto mais próximo. O rosado vem do UV da aréola e só entra no fundo, onde o peso nos
     bones `cf_J_Vagina_*` passa de 0,6. **Armadilha:** o UV3 (e o UV2) da pele é dividido por lado,
     com uma ilha da textura pra cada lado e a emenda na linha do meio. Interpolar por 1/d² misturava
     as duas ilhas e amostrava lixo entre elas. É o que desenhava os pelos em zigue-zague nos
     personagens que têm textura de pelos. No primeiro personagem não aparecia.
  8. Em cada canto do buraco, o ponto mais próximo da borda da peça solda no canto se estiver a menos
     de 30% do segmento, com no máximo 1 por canto. Sem isso, 15 de 103 triângulos de pele subdividida
     ficavam com ângulo menor que 2°, e lascas assim viram do avesso conforme a proporção do corpo.
  9. Os leques dos cantos são orientados **por propagação**: cada triângulo orientado serve de
     referência pro vizinho. Antes, um triângulo que só encostava em outro do leque ficava do avesso e
     virava um buraco escuro com o backface culling.

  **Captura da pose real (F9).** O `Genitais.cs` grava em OBJ a malha DEFORMADA (`BakeMesh`) do corpo
  de cada personagem, em `BepInEx/genital_dump/`, e registra no log o `QualitySettings.skinWeights` (o
  jogo usa `FourBones`). A ordem dos vértices é a do `.bin`, então dá pra fazer o seguinte:
  - `confere_captura_pose_captura.py` recupera por mínimos quadrados a matriz de skin de cada bone da
    pose capturada. Reposando vértices que ficaram fora do ajuste, o erro foi de 0,00005;
  - `confere_captura_avalia_real.py` e `confere_captura_avalia_bin.py` aplicam essa pose REAL em qualquer
    versão nova da malha, sem precisar reiniciar o jogo.

  O que a captura mostrou, e que a pose simulada não mostrava:
  - **Orelhas na borda da peça** (triângulo com 2 arestas na borda). O vértice do meio soldava num
    canto do buraco, e o triângulo ficava com os 3 vértices na borda: uma linha parada, com pesos
    diferentes, que na pose abria como uma aba. `sem_orelhas()` as tira a cada rodada; com isso
    `RODADAS` = 8, porque 4 não convergia.
  - **Arestas rígidas do buraco**, cujos cantos têm pesos muito diferentes (coxa × virilha, L1 > 1,2).
    Um ponto inserido no meio delas sai da linha na pose, porque o skinning é linear, e dobra o leque de
    pele. Nelas, o ponto da borda solda no canto mais próximo.
  - **Aplainamento** perto da borda (`RAIO_APLAINA` = 0,12), pra tirar o vinco de cerca de 113° entre
    pele e peça. Tem uma trava: onde aplainar criaria uma dobra parada, o efeito é reduzido pela metade.
  - **Relevo** (`ELEVA_PECA` = 0,05, `RAIO_RELEVO` = 0,08): a parte de fora da vulva ficava ~5 mm
    DENTRO do corpo em relação à pele original, porque o corpo "Innie" do KK é rebaixado, e o miolo
    parecia uma cavidade. O relevo sobe o interior ao longo da normal da pele, com 0 na borda e o valor
    cheio a partir do raio. A mediana foi de -0,05 pro nível da pele (`confere_profundidade.py`).
    Transladar a peça inteira **não** adianta, porque o encaixe harmônico devolve o deslocamento.
  - **Transferência de relevo** (`TRANSFERE_RELEVO` = 1): mesmo com o relevo acima, os grandes lábios
    ficavam 2 a 8 mm abaixo da pele (p10 -0,08), e a cavidade continuava visível no jogo. **Medido:**
    no KK eles ficam RENTES à virilha lisa do corpo padrão (`o_body_a` sem ossos `cf_J_Vagina`, que
    vem no mesmo zipmod), com mediana 0 mm. Então cada ponto da peça guarda a altura que tem sobre a
    virilha lisa do KK, e essa altura é reaplicada sobre a pele do Amanatsu, na normal dela. Com isso
    a mediana ficou em +0,001, o p10 em -0,024, as dobras paradas caíram de 14 para 0 e as dobras na
    pose capturada caíram de 4 para 3 (o backup tinha 9).
    **Armadilha:** aplicar a altura projetada direto em cada vértice deixou a superfície ondulada no
    jogo, porque o triângulo mais próximo muda de um vizinho para outro e a normal salta junto. O
    `confere_aspereza.py` mede isso (distância até a média dos vizinhos, dividida pela aresta média):
    a mediana foi de 0,025 para 0,039. Agora só o DESLOCAMENTO é aplicado, suavizado com Taubin
    (`SUAVIZA_RELEVO` = 20 passos). A média simples encolhia o deslocamento e a cavidade voltava;
    o Taubin não encolhe. Resultado: aspereza de 0,028, a vulva rente à pele e 0 dobras paradas.
  - **Triângulos de canto do avesso** (triângulos escuros vistos no jogo, dos dois lados): num canto
    do buraco onde a pele é convexa (ângulo menor que 180°), se caem pontos da borda nas duas arestas
    do canto, o triângulo do leque entre eles cobre a pele em vez do buraco e fica do avesso. Com o
    backface culling, o que aparece é o fundo escuro. O `confere_virados.py` pega esses triângulos
    (as outras conferências só olhavam a peça). A correção: `monta` detecta o triângulo e remonta com
    esses pontos soldados no canto, o mesmo tratamento das arestas rígidas (parâmetro `forca`).
    Cortar a pele desses cantos (`DENTE_GRAUS`, desligado) **não** resolve: o buraco cresce e a pose
    piora. Resultado: os triângulos virados visíveis na costura caíram de 7 para 0 (sobram 3
    minúsculos no fundo da fenda, que já existiam em todas as versões) e as dobras na pose capturada
    caíram de 6 para 2.
  - **Ossos da vulva** (`OSSOS_VULVA` = 1), a base para a colisão. Os 12 `cf_J_Vagina_*` do KK, cada
    um pendurado no seu pivô como no KK, viram filhos do `cf_s_kokanskin`, que carrega 98% do peso da
    peça. Cada vértice interno recebe, da fatia do `cf_s_kokanskin`, a parte que o KK dá aos ossos da
    vulva, e essa parte vai a 0 na borda. O `.bin` ganhou um bloco opcional no fim com nome, pai e as
    matrizes de pivô e osso no espaço da malha. O plugin (`LigaOssos`) cria os transforms com local =
    bindpose do pai × matriz, e acrescenta os bindposes. Parados, eles movem igual ao pai.
    **Medido** (pose real F9): a malha fica idêntica à sem ossos (diferença máxima 0). No limite de
    4 ossos por vértice, o osso da vulva mais fraco devolve o peso ao pai. Cortar e renormalizar
    deslocava 11 vértices em até 0,015.
  - **Ânus, modo atual `cavity` (padrão):** cavidade procedural (`anus_cavity`). O leque do ânus é igual
    nas 9 variantes: 4 triângulos num losango de 0,066 × 0,032, 100% `cf_s_hip_ana`. Ele é trocado por
    `CAVITY_PROFILE`: beirada que vira círculo, 10 pregas radiais (`CAVITY_FOLDS`) e canal fechado a 0,40 de
    fundo. Os pontos novos na aresta do leque ficam sobre ela e, como tudo é do mesmo osso, não abre fresta.
    Todo o efeito cabe num círculo de raio 0,027, dentro do losango (raio inscrito de 0,029): pregas que
    iam até a borda do leque desenhavam o losango no jogo. Normal nova = normal suavizada original + desvio
    da forma. Em repouso fica **fechado**. Oito ossos `cf_J_Anus_*` na abertura, filhos do `cf_s_hip_ana`, abrem pela
    mesma colisão da vulva, só para os lados, até 80% do círculo, 0 triângulo virado nas 9 variantes. Medido no losango: com 0,7 ou
    mais os triângulos viram. O rosado só aparece no canal, abaixo de 0,015: com o limite em 0, as pregas
    rasas pintavam um losango no jogo. No plugin, o decalque `mnpa` é
    desligado junto com o `mnpb` e o `cf_s_hip_ana` conta como entrada no alongamento do pênis (a entrada
    mais perto do eixo vence).
  - **Ânus, histórico** (relevo/enxerto: no jogo, os dois viraram só uma mancha de cor.
    Nenhum doador do KK tem cavidade; a depressão é rasa demais para a malha grosseira do Amanatsu). **O que o doador tem:** os ossos `cf_j_ana`/`cf_s_ana` e uma
    depressão rasa, sem canal. O centro fica 3,6 mm abaixo do corpo liso do KK e a depressão zera a
    9 mm do centro (escala do KK). A pele do Amanatsu ali é quase plana e grosseira, mas já tem
    topologia de ânus: um vértice central e um anel a cerca de 0,08, em leque.
    - `relevo` (padrão): aplica o perfil radial medido no KK (× 8,18) aos vértices do próprio
      Amanatsu, ao longo da normal. Isso afunda o centro até -0,032, recalcula as normais da região
      e pinta o rosado pelo mesmo critério da vulva. Sem costura e igual em todas as variantes: só 4
      vértices se movem (o centro e as cópias de UV dele).
    - **Outros doadores medidos** (`compara_anus.py`): todos os corpos de uncensor do KK/KKS (Profundis,
      Moderchan, Nam, pacote SAC com clit/gash/innie/kupaa/meaty) têm praticamente o mesmo ânus: 60 a 90
      vértices num raio de 1,2 cm e 6 a 11 mm de depressão, sem canal e sem ossos próprios. O
      "sacinnie2" é o mais fundo (16 mm). Trocar de doador não traz mais detalhe.
    - `enxerto` (padrão para teste): com relevo NEGATIVO completando a profundidade que o encaixe
      tirava em 3 variantes, todas as 9 ficam com o centro entre -0,022 e -0,027, sem dobras em
      repouso. **Medido** nas 19 poses (`virada_poses_var.py`, variantes `o_onepi`): a área virada no
      ânus fica entre 0,0013 e 0,0048 (máximo 0,011). O relevo fica em 0.
    - `enxerto`: segunda passada do `monta` com a peça do KK recortada no raio `R_ANUS` = 0,012 (com
      0,02 já pegava 86 vértices da vulva do KK, que fica colada no ânus). Precisou de: janela de
      altura estreita (as paredes das nádegas caem dentro do contorno visto de baixo), relevo e
      aplainamento desligados (viravam o ânus numa saliência de +3 mm) e escolha da escala do buraco
      por variante (1,05 a 1,5). **Medido:** 6 variantes com depressão de -0,023 e sem defeitos, mas 3
      rasas ou salientes. Além disso, a pele grosseira exige um buraco 1,2 a 1,5x maior que a peça, com
      costura de triângulos longos.
  - **Tom rosado** (`ROSADO` = 0,005 a 0,025 de profundidade). **Medido:** o rosa do mamilo NÃO
    está na textura principal do corpo. A montagem da textura em tempo de jogo
    (`lib/chara/body/mat/create`) só tem pintura e bronzeado. O mamilo vem da máscara
    `cf_t_nip_0X` (`lib/chara/mt_nip_000_01.unity3d`), aplicada pelo shader no UV2 (terceiro canal,
    o `uv3` do Unity). Em volta do bico, o UV2 mapeia a máscara com o centro (0,5; 0,5) no bico:
    0,1 de UV2 dá cerca de 0,075 unidades no corpo. A virilha inteira fica em (0; 0), que é
    transparente. A máscara tem o azul na aréola (a cor vem da personagem) e o verde no bico. Os
    vértices da peça que ficam abaixo da pele recebem UV2 num raio de 0,22 → 0,08 da máscara, com a
    transição suave da própria borda dela. O resto fica em (0; 0,5), transparente, e a linha até o
    (0; 0) da pele não cruza a máscara. **Correção:** o resto da peça passou a receber o UV2
    PROJETADO da pele em volta. Com (0; 0,5) fixo, a faixa da costura (pele (0; 0) → peça (0; 0,5))
    vazou um contorno rosado numa segunda personagem (aréola/ajustes dela). Agora a costura tem o
    mesmo UV2 da pele original (0,707 do centro da máscara). **Armadilha:** o "rosado" antigo trocava o UV0 pelo da ponta
    do seio na textura principal, que é só pele; ainda por cima, a ponta do seio fica a 0,26 do bico.
  - **Correção final dessas abas (`TIRA_ABA`, padrão):** o fechamento do avesso feito só de cantos
    está DEITADO sobre a pele do dente. Invertido, a normal dele bate 1,0 com a dos vizinhos de pele,
    e os vértices opostos ficam do mesmo lado da aresta: é uma aba dupla, não um buraco. O shader de
    pele desenha as duas faces, então a aba aparecia escura ou clara por cima da pele, e também por
    dentro do corpo. Remover a aba mantém a malha idêntica à versão anterior (vértices iguais, 3
    triângulos a menos). **Medido** nas 19 poses capturadas: área virada 0,0001 em todas. Com o
    corte, era de 0,0003 a 0,0036, e o corte **serrilhou o contorno no jogo**, porque o buraco
    avançava na dobra da coxa; ficou desligado (`CORTA_DENTE=1`). Achatar o dente virava um vizinho.
  - **Fechamento feito só de cantos, do avesso** (os triângulos vistos de fora na virilha, dos dois
    lados). Quando os dois pontos da borda de um trecho são soldados em cantos, o fechamento entre
    eles fica feito só de vértices da pele. Onde o buraco tem um "dente", ele cobre a pele e sai do
    avesso. **Armadilha:** todos os vértices desse triângulo são < n0, e o `confere_virados` (que
    filtrava "toca vértice novo") não o pegava. O critério agora é o triângulo não existir na malha
    original. A correção (`cortar` em `monta` / `_buraco_e_encaixe`) corta do buraco só os
    triângulos de pele que encostam nesse fechamento, e só fica com o corte se a área virada cair,
    comparando as duas malhas prontas. **Medido** com `confere_captura_virada_poses.py`, que olha a
    área virada no repouso e em cada uma das 16 poses capturadas: publicada 0,004 / 0,007 a 0,014,
    nova 0,0001 / 0,0003 a 0,0036. **Armadilha 2:** a contagem de "dobras na pose" do
    `avalia_bin` ignora o que já nasce virado em repouso e chegou a apontar a versão nova como pior.
  - **Profundidade do pênis** (`AlongaPenis`; configuração `[Genitals] ExtraDepth`, padrão
    0,15). **Medido** na captura F9 durante a penetração: os ossos do pênis não mudam de escala. É a
    ANIMAÇÃO do jogo que puxa o `cf_j_dan109_00` para 24% da distância de repouso até o `dan101`.
    O pênis original era uma cápsula curta, e isso evita que ele atravesse o corpo feminino. O
    `LookAtPenis` só gira. A cada frame, depois da animação, o mod devolve essa fração do comprimento
    que ela tirou. O repouso vem dos bindposes, porque a pose atual já pode estar encurtada. Se a
    animação não reescrever o osso num frame, o mod parte do valor anterior e não acumula.
  - **Armadilha (forma redonda perdida em repouso):** o esqueleto FEMININO também tem
    `cf_j_dan101_00`/`dan109` na virilha. A cápsula de pênis criada na heroína empurrava a própria
    vulva no limite (0,15) o tempo todo, e o log do F9 mostrou isso (`F`, `L/R.001`, `L/R.002`
    empurrados sem ninguém perto). Agora a cápsula do pênis só atua com o `o_dankon` daquele
    personagem visível (`Capsula.Vis`), o que também cobre o masculino com o pênis escondido. O F9
    grava no log o estado de cada osso da vulva.
  - **Colisão, versão atual** (`AtualizaFisica`, uma vez por frame): o "push" do BetterPenetration.
    Cada osso da vulva sai da posição de repouso até ficar fora das cápsulas (dedos e pênis). O
    empurrão tem limite de 0,15 e o osso chega a ele suavemente (taxa de 20/s). **Por que não o
    `DynamicBone`** (a primeira versão, descrita no item abaixo): na captura F9 ele deu 56 dobras.
    O braço pivô → osso é longo (cerca de 3 unidades), a trava de rigidez deixa o osso ir 1,2 vez
    isso, e a animação do jogo enfia os dedos fundo na virilha. **Pesos:** os pesos do KK copiados
    vértice a vértice rasgavam em vincos com qualquer empurrão. Agora eles passam por 40 passos de
    média sobre a malha (`SUAVIZA_PESO_VULVA`), com transição até a pele 3x mais larga
    (`FADE_VULVA`). **Medido** com `sim_empurra.py` (scratchpad), que reproduz o empurrão na pose
    exata da captura: de 33 a 37 dobras antes, 3 depois (parado: 1).
  - **Colisão, primeira versão** (`LigaFisica` / `CriaColisores`), feita como o BetterPenetration faz no KK: ele NÃO
    calcula a deformação na mão. Cada pivô recebe um `DynamicBone` (o jogo tem o mesmo componente,
    versão 1.3), e os dedos e o pênis recebem cápsulas `DynamicBoneCollider`.
    - Parâmetros lidos do próprio doador: 60/s, amortecimento 0,85, elasticidade 0,35, rigidez 0,4,
      inércia 0,95; F e B travam o eixo X; o raio do KK × curva de fim de cadeia 1,235 × 8,18.
    - Cápsulas nos ossos `cf_j_index/middle/ring 02/03` das duas mãos (eixo X, centradas no meio da
      falange; o BP centra na junta e a ponta ficava de fora) e em `cf_j_dan101_00` → `dan109` (eixo Z).
    - Raios medidos nas malhas do Amanatsu: dedo 0,045 (a falange mede 0,035 a 0,08; o valor do BP
      convertido dá 0,045) e pênis 0,21.
    - **Armadilha:** no `DynamicBone` as listas (`m_Colliders`, `m_notRolls`, `m_Roots`,
      `m_Exclusions`) começam nulas. Quem as preenche é o prefab, então quem adiciona o componente por
      código tem de criá-las.
  - **Travas** do aplainamento e do relevo: onde o passo cria dobra nova, ou faz uma dobra que já
    existia crescer, a força é reduzida pela metade naqueles vértices, até zerar. **Armadilha:** a
    redução tem de ir no REPRESENTANTE (`kcanon`) do vértice, porque o `[kcanon]` usa o valor dele;
    reduzir no duplicado não tinha efeito.
  - Resultado na pose capturada: as dobras envolvendo geometria nova caíram de 15 pra 6. Nessa mesma
    pose, a **pele original do jogo** tem 28 dobras, no vinco da coxa, e elas existem sem o mod.
  - Testado e descartado: alisar pesos (não muda nada), suavizar a geometria com laplaciano (piora de 6
    pra 10 dobras) e soldar mais pontos nos cantos (não muda nada).

  **Conferências** (em `genitais/`):
  - `confere_topologia.py`: bordas abertas e arestas com mais de 2 triângulos, comparadas com a malha original;
  - `confere_sentido.py`: consistência do enrolamento;
  - `confere_pose.py`: pose realista, comparada com a pele original.

  Os scripts apontam pro zip do BP no scratchpad; troque o caminho se for regerar.

  O conversor confere a topologia de cada variante. As versões anteriores cortavam pelo decalque,
  pegavam a borda externa do anel e costuravam por ângulo, o que gerava as lascas vistas no jogo.
- **Cor:** o UV0 aponta pra pele lisa. No feminino, as partes pesadas em `cf_J_Vagina_*` usam o UV do
  mamilo, que dá o tom rosado da própria textura. O material é a pele do personagem.
- Com a vulva ativa, o decalque `mnpb` é desligado.
- **Armadilha:** a cor de vértice do corpo é `UNorm8` no bundle, e o `MeshHandler` do UnityPy devolve
  valores de 0 a 255. O shader `AL/skin_body` usa essa cor, então sem dividir por 255 a malha
  "estoura" no jogo.

Pra regenerar (precisa do `KKS_BetterPenetration.zip`, extraído):
`py -3.13 genitais/converte_kk.py <mods/BetterPenetration> lib/chara/body/body_00.unity3d.bak`
(e o mesmo pra `converte_kk_fem.py`; `--png` salva renders de conferência).
Malhas: Animal42069 (BetterPenetration) e DeathWeasel (SoS). Crédito obrigatório se distribuir.

---

## Freemode

### Fluxo final

1. `TitleScene.Awake` (postfix): clona o botão "Memory" do título e renomeia pra "Freemode".
2. Clique: `TitleScene.GotoMemoryScene(false)` abre a Recollection (回想), que vira só menu de escolha.
3. `MemoryItem.SetData` (postfix): libera todos os locais (`AlreadyRead = true` e `Selectable` ativo).
4. `MovieManager.PlayShortMove` (prefix, `return false`): o clipe nunca roda. No lugar dele, `MontaH`:
   - **período**: toggles manhã/tarde/noite → `1`/`2`/`3`
   - **mapa**: `ContentProvider.ShortMovieInfoContents[CurrentMemory.Id].MapID`
   - esconde a UI (`_canvas`)
   - **heroína**: `Actor.Rent<Heroine>(new NPCData{ HumanData = _human, ... }, sex: 1, Game.AccessorySlotNum)`
   - `MapManager.Instance.ChangeMapAsync(mapa, periodo, false, null, true, false)`
   - **player**: `Game.Instance.Player` não existe fora do modo história, então aluga um com uma
     carta masculina aleatória (`DefaultData/0/chara/male` + `UserData/chara/male`) via
     `HumanData.LoadCharaFile` + `Actor.Rent<Player>(new PlayerData{...}, sex: 0, ...)`
   - `HScene.Create()` → `new H.Parameter(...)` → `HScene.InitializeAsync(param, token)`.
     **Esse `InitializeAsync` só retorna quando o jogador encerra a H.**
5. `Volta`: repete a limpeza que o fim do `PlayShortMove` faz pra voltar à Recollection. Sem ela,
   a tela fica parada ao encerrar a H.
   - `Game.SceneFade(Fade.In)`
   - `Dispose()` + `Actor.Return(...)` da heroína e do player alugado
   - `Game.ClearChaGrid()`
   - `Sound.Stop(Type.ENV)`
   - `_canvas` ligado de novo
   - `Game.SceneFade(Fade.Out)`

   A chamada `ShortMovie.Release` (`sub_180C0B540`) fica de fora, porque nunca carregamos o clipe.

Tudo isso é a receita do próprio jogo: os passos 1 a 5 de `MontaH` são os mesmos que
`PlayShortMove` executa, e a montagem da H copia `EventManager.HDetail`.

### Como o jogo inicia uma H (decompilado)

Modo história, do clique até a cena:

```
OutlinableManager._currentCol           (heroína na mira)
  → ActionMapScene.Map3DToCommandScene   (único chamador de CommandScene.Open)
  → CommandScene.Open(mode, heroina)     (painel de comandos)
  → botão H = CommandUI.<Initialize>b__60_5 → EventManager.H(CommandUI._eventParam)
  → StartProc (ADV de aceite) → PlayADV (GetHStartAsset) → HProc
  → EventManager.HDetail                 (ÚNICO chamador de HScene.InitializeAsync)
  → HScene.InitializeAsync(new H.Parameter(state, pattern, category, place, camera, humans), token)
```

- `EventManager.H` sempre toca ADV antes. Chamar `HAsync` fora do contexto do painel de
  comandos não faz nada visível, porque a espera do ADV nunca resolve.
- `HDetail` monta `Humans = [Player.Human, parceira, terceiro_ou_null]`. **O 3º slot é o do 3P.**
  Repetir a parceira ali faz a HScene esconder a heroína. Tem que ser `null`
  (o construtor do `Parameter` filtra os nulos).
- A câmera pode ser null no nível nativo, mas o wrapper gerenciado exige objeto. Na Recollection
  não existe câmera, então usamos `Camera.main ?? FindObjectOfType<Camera>() ?? new Camera`.
- `H.PlaceType`: Floor=0, Chair=1, Desk=2, BeachChair=3, Poolside=4, Pool=5, Sea=6. O Freemode usa `Floor`.
- Botões do `CommandUI` → lambdas `<Initialize>b__60_N`, na ordem Talk=0, Listen=1, HTopic=2,
  Invite=3, Massage=4, **H=5**, With=6, WaterGun=7, End=8. Todas passam por
  `OnClickCommonCondition`, que escuta o `onClick` via `UnityEventHandlerAsyncEnumerable`, e por
  `IsClick`, que bloqueia enquanto alguma animação de UI roda.

### Recollection (`AL.Memory`)

| membro | offset | observação |
| :--- | :--- | :--- |
| `MovieManager.CurrentMemory` | 0x20 | `MemoryItem` clicado (`ClickMemory`) |
| `MovieManager._heroine` | 0x90 | preenchido só dentro de `PlayShortMove` (ator alugado) |
| `MovieManager._human` | 0x98 | `HumanData` da carta, gravado por `SetCharaData` |
| `MovieManager._path` | 0xA0 | caminho completo do `.png` da carta |
| `MemoryItem.Id` | 0x30 | chave de `ShortMovieInfoContents` |
| `MemoryItem.AlreadyRead` | 0x68 | `ClickMemory` sai cedo se for falso |

- `MovieManager.PlayActive()` só liga ou desliga o botão play. Quem toca o clipe é `PlayShortMove()` (async void).
- `MemoryItem.SetData` decide o bloqueio consultando o HashSet de IDs desbloqueados e desativa
  `Selectable` direto, sem passar por `SetSelectable`.
- `ALShortMovieContent { ID 0x10, MapID 0x14, StandbyAnimID 0x18 }`.

### Armadilhas do IL2CPP interop (valem pro projeto todo)

1. **`Nullable<T>` e `CancellationToken` são classes no interop.** Passar `null`/`default`
   estoura `NullReferenceException` em `Il2CppObjectBaseToPtrNotNull`, dentro do wrapper, antes
   de chegar no código nativo. Use `new Il2CppSystem.Nullable<T>()` e
   `new Il2CppSystem.Threading.CancellationToken()`. O IDA não mostra isso, porque o erro está
   no wrapper C#. Achamos com Mono.Cecil lendo o IL de `BepInEx/interop/Assembly-CSharp.dll`.
2. **Campo privado vira propriedade.** `AccessTools.Field(typeof(X), "_campo")` devolve `null`.
   Acesse direto (`instancia._campo`), porque o interop expõe como propriedade pública.
3. **`IReadOnlyList<T>` do interop não tem `Count`/`GetEnumerator` utilizáveis.** Use o campo
   concreto por baixo (ex.: `Manager.Game.Instance._heroines`, que é `List<Heroine>`).
4. **`RemoveAllListeners()` não remove listener persistente** de um `Button` clonado. Troque o
   evento inteiro: campo `m_OnClick` = `new Button.ButtonClickedEvent()`.
5. **Delegate gerenciado passado pro IL2CPP precisa de referência viva** (lista estática), senão
   o GC recolhe e o clique vira no-op.
6. `await` em `UniTask` funciona direto nos wrappers do interop.

### Becos sem saída (não repetir)

- Forçar `IsH = true` no `MapSelectUI` da `ActionMapScene`: crasha sempre.
- Deixar o `PlayShortMove` rodar e suprimir só `ShortMovie.Play`/`PlayAsync`: o resto da
  coroutine (troca de mapa, limpeza, devolução do ator ao pool) corre em paralelo com a HScene e
  crasha no código nativo, sem exceção .NET.
- Rodar a HScene junto com o `ShortMovie`: o `PlayableDirector` do clipe disputa câmera e input.
- Procurar a heroína viva no mapa do modo história: o título carrega jogo novo, então a lista
  `Game._heroines` vem vazia.
- Ler `MovieManager._heroine` por reflexão: nunca veio preenchido (ver armadilha 2).

---

## Método de análise

Ler o código antes de testar, porque cada teste custa um reinício do jogo. Ferramentas em `../ida_scripts/`:

| arquivo | uso |
| :--- | :--- |
| `full_rename_and_dump.py` | aplica `script.json` + `il2cpp.h` do Il2CppDumper no `GameAssembly.dll` e decompila por prefixo de namespace (~15 min) |
| `out/GameAssembly.dll.i64` | base do IDA já analisada e renomeada; reusar evita os 15 min |
| `xrefs.py` | lista quem chama os RVAs em `TARGETS` e decompila os chamadores |
| `decomp.py` | decompila os RVAs em `TARGETS` |

Rodar headless (o executável é `idat.exe`; o `idat64.exe` não existe no 9.1):

```
"C:\Program Files\IDA Professional 9.1\idat.exe" -A -S"<script.py>" -L"<log.txt>" "<...>\ida_scripts\out\GameAssembly.dll.i64"
```

RVAs e offsets de campo saem de `Il2CppDumper/Dump0/dump.cs` (comentários `// RVA:` e `// 0x..`).
Para o que o wrapper do interop faz com os argumentos, leia o IL com Mono.Cecil
(`BepInEx/core/Mono.Cecil.dll`) sobre `BepInEx/interop/*.dll`.

RVAs usados no Freemode:

| método | RVA |
| :--- | :--- |
| `EventManager.HAsync` | 0xBF1AA0 |
| `EventManager.<HDetail>d__46.MoveNext` | 0xBFB960 |
| `CommandScene.Open(WaitMode, Heroine)` | 0xBE4AE0 |
| `CommandUI.Open` / `OnClickCommonCondition` / `IsClick` / `Initialize` | 0xC1CC70 / 0xC1D420 / 0xC1E7A0 / 0xC1D8C0 |
| `CommandUI.<Initialize>b__60_5` (botão H) | 0xC217E0 → state machine 0xC24F10 |
| `MovieManager.ClickMemory` / `SetCharaData` / `PlayActive` / `PlayShortMove` | 0xD7C3B0 / 0xD7C730 / 0xD7C7F0 / 0xD7C950 |
| `MovieManager.<PlayShortMove>d__25.MoveNext` | 0xD7CCC0 |
| `H.Parameter..ctor` | 0xDC9510 |
| `CharacterGrid.set_Visible` / `set_VisibleImmediate` | 0xA70200 / 0xA700D0 |
