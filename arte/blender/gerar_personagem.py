"""
Gera o personagem v3 do Jogo da Vida com o MPFB (MakeHuman Plugin for Blender).

Ajustado a partir de fotos do Matheus: cabelo escuro de cachos curtos com laterais em
degradê, barba cheia aparada, óculos de meia-armação, pele clara, porte atlético forte
(~1,75 m), camiseta justa e calça pretas de treino e tênis pretos de sola branca.
Todas as características ficam nas constantes do bloco "configuração" logo abaixo.

Cabelo e barba são "shell fur": várias cascas sobre a pele, cada uma com recorte por alpha.
O alpha da textura guarda a "altura" do fio (0..1); cada casca mostra só os pixels acima do
seu limite (M_Cabelo < M_Cabelo_Meio < M_Cabelo_Topo; M_Barba < M_Barba_Externa). No Unity
o limite é o _Cutoff de cada material (ver ImportarPersonagem.cs). Por baixo das cascas a
pele recebe uma pintura do cabelo/barba com degradê, o que dá as bordas suaves.

Uso (Blender 5.2 com a extensão MPFB instalada e os "makehuman system assets" no MPFB):

    blender -b --python arte/blender/gerar_personagem.py -- [--assets-zip CAMINHO.zip] [--sem-render]

Saídas (relativas à raiz do repositório):
    arte/blender/personagem.blend
    unity/Assets/JogoDaVida/Arte/Personagem/Personagem.fbx (+ texturas na mesma pasta; texturas
        antigas que não são mais usadas e os materiais extraídos pelo Unity são apagados)
    capturas/personagem-frente.png, personagem-34.png, personagem-rosto.png,
    capturas/personagem-rosto-sorriso.png, personagem-rosto-cansado.png

    capturas/personagem-jogo.png (pose relaxada, enquadramento da câmera do jogo)

Assets: base, pele, olhos, sobrancelhas, cílios, roupa e tênis são dos "system assets"
da MakeHuman (CC0). Cabelo, barba e óculos são gerados por este script (sem asset de terceiros).
O resultado é determinístico (o ruído usa semente fixa).
"""

import bpy
import bmesh
import glob
import math
import os
import sys
import zipfile
import numpy as np
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree
from mathutils.interpolate import poly_3d_calc

from bl_ext.blender_org.mpfb.services import (
    LocationService, AssetService, HumanService, TargetService)
from bl_ext.blender_org.mpfb.entities.objectproperties import HumanObjectProperties
from bl_ext.blender_org.mpfb.entities.clothes.mhclo import Mhclo

# ---------------------------------------------------------------- configuração

RAIZ = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
BLEND = os.path.join(RAIZ, "arte", "blender", "personagem.blend")
PASTA_FBX = os.path.join(RAIZ, "unity", "Assets", "JogoDaVida", "Arte", "Personagem")
FBX = os.path.join(PASTA_FBX, "Personagem.fbx")
CAPTURAS = os.path.join(RAIZ, "capturas")

RIG = "mixamo_unity"          # nomes "mixamorig:*", mapeia direto no Unity Humanoid
MAX_TEXTURA = 2048
SEMENTE = 7                   # ruído da barba reproduzível

# --- Corpo: homem ~25 anos, atlético e musculoso (ombros largos, cintura fina), ~1,75 m.
ALTURA_ALVO = 1.75            # metros, do calcanhar ao topo da cabeça (sem cabelo/tênis)
MACROS = {
    "gender": 1.0,
    "age": 0.5,
    "muscle": 0.82,
    "weight": 0.45,
    "proportions": 0.85,      # proporções "ideais": ombro largo, cintura mais fina
    "height": 0.45,           # ponto de partida; o script ajusta até chegar em ALTURA_ALVO
    "cupsize": 0.5,
    "firmness": 0.5,
    "race": {"asian": 0.10, "caucasian": 0.60, "african": 0.30},
}
# Alvos extras (pasta targets do MPFB -> peso): deltoides, bíceps/tríceps, peitoral, dorsal,
# pescoço mais grosso. A cintura fica como está. O trapézio é feito à mão em reforcar_trapezio().
CORPO_ALVOS = {
    "arms/l-upperarm-shoulder-muscle-incr": 0.80, "arms/r-upperarm-shoulder-muscle-incr": 0.80,
    "arms/l-upperarm-muscle-incr": 0.75, "arms/r-upperarm-muscle-incr": 0.75,
    "arms/l-upperarm-scale-horiz-incr": 0.25, "arms/r-upperarm-scale-horiz-incr": 0.25,
    "arms/l-upperarm-scale-depth-incr": 0.25, "arms/r-upperarm-scale-depth-incr": 0.25,
    "arms/l-lowerarm-muscle-incr": 0.45, "arms/r-lowerarm-muscle-incr": 0.45,
    "torso/torso-muscle-pectoral-incr": 0.70,
    "torso/torso-muscle-dorsi-incr": 0.45,
    "torso/measure-shoulder-dist-incr": 0.25,
    "neck/neck-scale-horiz-incr": 0.45,
    "neck/neck-scale-depth-incr": 0.30,
}
TRAPEZIO_ALTURA = 0.010       # quanto (m) o trapézio sobe entre o pescoço e o ombro

# --- Pele clara: a textura caucasiana jovem quase como é (só um pouco menos avermelhada).
PELE = "skins/young_caucasian_male/young_caucasian_male.mhmat"
PELE_COR_MEDIA = (0.88, 0.675, 0.555)   # média alvo da textura (sRGB 0..1); original ~(0.90, 0.67, 0.54)

# --- Olhos castanhos (a textura brownlight escurecida; a brown original sai avermelhada).
TEXTURA_DOS_OLHOS = ("brown_eye.png", "eyes/materials/brownlight_eye.png")
OLHOS_GANHO = (0.62, 0.45, 0.33)        # multiplicador aplicado à íris

# --- Sobrancelhas escuras, grossas e bem visíveis.
SOBRANCELHAS = "eyebrows/eyebrow009/eyebrow009.mhclo"
SOBRANCELHAS_COR = (0.030, 0.022, 0.018)
SOBRANCELHAS_ALFA = 1.6                  # reforço do alfa da textura (fios mais cheios)

# --- Cabelo: cachos curtos escuros. Cascas geradas por raios a partir do centro da cabeça.
CABELO_COR = (0.072, 0.053, 0.041)       # castanho quase preto
CABELO_GRADE = 35                        # células da grade (em cada eixo) do disco de UV
CABELO_THETA_MAX = 2.55                  # rad: até onde a grade vai a partir do topo
# comprimento (m) dos cachos pela altura relativa na cabeça (0 = meio da orelha, 1 = topo)
CABELO_ALTURA = [(0.00, 0.0012), (0.25, 0.0020), (0.45, 0.0045), (0.65, 0.0120),
                 (0.82, 0.0175), (1.00, 0.0200)]
# cascas: (material, fração do comprimento, limite de alfa)
CABELO_CASCAS = [("M_Cabelo", 0.22, 0.14), ("M_Cabelo_Meio", 0.60, 0.42),
                 ("M_Cabelo_Topo", 0.88, 0.68)]
CABELO_CACHO_RAIO = (0.0040, 0.0075)     # m, raio dos tufos de cachos
CABELO_ANEL_RAIO = (0.0013, 0.0026)      # m, raio de cada cacho (anel)
CABELO_NA_PELE = 0.88                    # quanto a pintura do couro cabeludo escurece a pele

# --- Barba cheia, escura e aparada: pintura na pele + 2 cascas com fios.
BARBA_COR = (0.120, 0.088, 0.066)
BARBA_CASCAS = [("M_Barba", 0.0012, 0.0032, 0.30, 0.10),          # (material, afastamento
                ("M_Barba_Externa", 0.0030, 0.0068, 0.62, 0.70)]  #  mín/máx, alfa, peso mín.)
BARBA_FIOS = 55000                       # fios desenhados na textura das cascas
BARBA_FIO_PX = (6, 14)                   # comprimento dos fios em pixels (textura 2048)
BARBA_NA_PELE = 0.80                     # quanto a pintura da barba escurece a pele
BARBA_BORDA_TOPO = 0.020                 # largura (m) do degradê na bochecha
BARBA_BORDA_PESCOCO = 0.022              # e no pescoço
# contorno de cima (offsets em m a partir do centro dos lábios), ver pesos_da_barba()
BARBA_TOPO = [(0.0, 0.021), (0.022, 0.020), (0.035, 0.021), (0.050, 0.030), (0.062, 0.042),
              (0.072, 0.062), (0.090, 0.078)]   # (|x|, altura máx.)

# --- Óculos: armação retangular fina e escura, meia-armação (aro só em cima), sem lentes.
OCULOS_LENTE_LARGURA = 0.050             # m
OCULOS_LENTE_ALTURA = 0.028
OCULOS_EXPOENTE_CANTO = 6.0                # superelipse: 2 = oval, maior = mais retangular
OCULOS_ALTURA_LENTE = -0.008             # centro da lente em relação ao centro dos olhos (m)
OCULOS_PONTE = 0.016                     # distância entre as lentes
OCULOS_DISTANCIA_OLHO = 0.013            # da frente do olho até a lente
OCULOS_CURVA = 7.0                       # graus que cada lente gira para trás (wrap)
OCULOS_ARO_TOPO = 0.0019                 # meia-espessura do aro na parte de cima
OCULOS_ARO_LADO = 0.0010                 # nas pontas que descem pelas laterais
OCULOS_FOLGA = 0.0025                    # distância mínima da pele
OCULOS_COR = (0.012, 0.012, 0.014)
OCULOS_COM_LENTES = False                # lentes "leitosas" atrapalham; só a armação
OCULOS_LENTE_ALFA = 0.02

# --- Roupa: camiseta preta lisa + calça preta justa (jogger) + tênis pretos de sola branca.
ROUPA = "clothes/male_casualsuit06/male_casualsuit06.mhclo"
TEXTURA_DA_ROUPA = "male_casualsuit06_diffuse.png"
LOGOS_NA_CAMISETA = [          # caixas (x0, y0, x1, y1) em frações da imagem, origem em cima
    (0.18, 0.09, 0.31, 0.18),
    (0.43, 0.08, 0.65, 0.21),
    (0.71, 0.12, 0.78, 0.19),
]
ROUPA_PRETO = (0.030, 0.030, 0.032)      # cor base; os detalhes da textura original ficam sutis
ROUPA_DETALHE = 0.45                     # quanto do desenho original (costuras, dobras) aparece
CALCA_AFUNILAR = [(0.00, 0.60), (0.12, 0.62), (0.30, 0.80), (0.48, 1.00)]  # (altura m, escala)
TENIS = "clothes/shoes06/shoes06.mhclo"
TENIS_SOLA_ALTURA = 0.030                # faces abaixo disso (m, acima do chão) são sola
TENIS_PRETO = (0.025, 0.025, 0.028)
TENIS_SOLA = (0.88, 0.88, 0.86)
CAMISETA_FOLGA = 0.0035                  # distância alvo (m) da manga até a pele
CAMISETA_APERTO_MANGA = 0.80             # quanto da folga extra some nas mangas (0..1)
CAMISETA_APERTO_TRONCO = 0.35            # e no tronco

# Aspereza (roughness) por material: tira o aspecto de plástico do cabelo, barba e tênis.
ASPEREZA = {"M_Corpo": 0.55, "M_Sobrancelhas": 0.9,
            "M_Roupa": 0.85, "M_Tenis": 0.6, "M_Oculos": 0.3}

# (arquivo, tipo do MPFB, nome final)
ASSETS = [
    ("eyes/low-poly/low-poly.mhclo", "Eyes", "Olhos"),
    (SOBRANCELHAS, "Eyebrows", "Sobrancelhas"),
    ("eyelashes/eyelashes01/eyelashes01.mhclo", "Eyelashes", "Cilios"),
    (ROUPA, "Clothes", "Roupa"),
    (TENIS, "Clothes", "Tenis"),
]

# Shape keys. Corpo: valores de macro alterados em volta do padrão acima.
# Expressão: unidades de expressão do MakeHuman.
FORMAS_DO_CORPO = {
    "Corpo_Musculoso": {"muscle": 1.0},
    "Corpo_Magro": {"weight": 0.0, "muscle": 0.6},
    "Corpo_Gordo": {"weight": 1.0},
}
EXPRESSOES = {
    "Rosto_Sorriso": {"mouth-corner-puller": 1.0, "mouth-upward-retraction": 0.25},
    "Rosto_OlhosCansados": {"eye-left-closure": 0.25, "eye-right-closure": 0.25,
                            "eyebrows-left-inner-up": 0.35, "eyebrows-right-inner-up": 0.35},
}

# Nomes finais das texturas exportadas (arquivo de origem -> arquivo no Unity).
NOMES_DAS_TEXTURAS = {
    "young_lightskinned_male_diffuse.png": "pele_diffuse.png",
    "brownlight_eye.png": "olhos_diffuse.png",
    "eyebrow009.png": "sobrancelhas_diffuse.png",
    "eyelashes01.png": "cilios_diffuse.png",
    TEXTURA_DA_ROUPA: "roupa_diffuse.png",
    "male_casualsuit06_normal.png": "roupa_normal.png",
    "shoes06_diffuse.png": "tenis_diffuse.png",
}


def argumentos():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    zip_assets, render = None, True
    i = 0
    while i < len(argv):
        if argv[i] == "--assets-zip":
            zip_assets = argv[i + 1]
            i += 1
        elif argv[i] == "--sem-render":
            render = False
        i += 1
    return zip_assets, render


# ---------------------------------------------------------------- utilidades

def log(*a):
    print("[personagem]", *a, flush=True)


def limpar_cena():
    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o, do_unlink=True)
    for col in (bpy.data.meshes, bpy.data.armatures, bpy.data.materials, bpy.data.images,
                bpy.data.cameras, bpy.data.lights):
        for item in list(col):
            col.remove(item)


def garantir_assets(zip_assets):
    dados = LocationService.get_user_data()
    marcador = os.path.join(dados, "clothes", "male_casualsuit06")
    if not os.path.isdir(marcador):
        if not zip_assets or not os.path.isfile(zip_assets):
            raise RuntimeError("Assets do sistema MakeHuman não instalados. Passe --assets-zip "
                               "makehuman_system_assets_cc0.zip (static.makehumancommunity.org)")
        log("instalando assets de", zip_assets)
        with zipfile.ZipFile(zip_assets) as z:
            z.extractall(dados)
        AssetService.update_all_asset_lists()
    return dados


def coords_da_mistura(obj):
    """Posição dos vértices com todas as shape keys aplicadas."""
    if not obj.data.shape_keys:
        return [v.co.copy() for v in obj.data.vertices]
    tmp = obj.shape_key_add(name="_tmp", from_mix=True)
    co = [d.co.copy() for d in tmp.data]
    obj.shape_key_remove(tmp)
    return co


def unidades_por_raca(nome_unidade):
    raiz = os.path.join(LocationService.get_mpfb_data("targets"), "expression", "units")
    return [(os.path.join(raiz, raca, nome_unidade + ".target.gz"), peso)
            for raca, peso in MACROS["race"].items()]


def triangulos(obj):
    me = obj.data
    me.calc_loop_triangles()
    return len(me.loop_triangles)


def mover_vertices(obj, desloc):
    """Soma desloc[i] (Vector) ao vértice i na malha e em todas as shape keys."""
    for i, d in desloc.items():
        obj.data.vertices[i].co += d
    if obj.data.shape_keys:
        for kb in obj.data.shape_keys.key_blocks:
            for i, d in desloc.items():
                kb.data[i].co += d


def indices_do_grupo(obj, nome):
    g = obj.vertex_groups.get(nome)
    if g is None:
        return []
    return [v.index for v in obj.data.vertices
            if any(x.group == g.index and x.weight > 0 for x in v.groups)]


def bvh_da_pele(base):
    """BVH só da pele (grupo "body"), sem helpers. Usa as coordenadas atuais da malha."""
    corpo = set(indices_do_grupo(base, "body"))
    verts = [v.co.copy() for v in base.data.vertices]
    polys = [list(p.vertices) for p in base.data.polygons if all(i in corpo for i in p.vertices)]
    return BVHTree.FromPolygons(verts, polys)


def bvh_atual(obj):
    """BVH com todas as faces da malha (coordenadas atuais, sem shape keys)."""
    verts = [v.co.copy() for v in obj.data.vertices]
    polys = [list(p.vertices) for p in obj.data.polygons]
    return BVHTree.FromPolygons(verts, polys)


def suave(x):
    x = np.clip(x, 0.0, 1.0)
    return x * x * (3 - 2 * x)


def rampa(d, largura):
    """0 -> 1 em volta de d = 0, com a largura dada (degradê suave)."""
    return suave(np.asarray(d, np.float64) / largura + 0.5)


class Ruido3D:
    """Ruído suave e determinístico (soma de senoides em direções aleatórias), saída ~[-1, 1]."""

    def __init__(self, comprimento, semente, n=7):
        rng = np.random.default_rng(semente)
        d = rng.normal(size=(n, 3))
        d /= np.linalg.norm(d, axis=1, keepdims=True)
        self.k = d * (2 * math.pi / comprimento) * rng.uniform(0.7, 1.4, (n, 1))
        self.fase = rng.uniform(0, 2 * math.pi, n)
        self.n = n

    def __call__(self, p):
        p = np.atleast_2d(np.asarray(p, np.float64))
        return np.sin(p @ self.k.T + self.fase).sum(1) / math.sqrt(self.n / 2)


def ampliar(img, tamanho):
    """Reamostra (bilinear) uma matriz 2D quadrada para tamanho x tamanho."""
    n = img.shape[0]
    t = (np.arange(tamanho) + 0.5) * n / tamanho - 0.5
    i0 = np.clip(np.floor(t).astype(int), 0, n - 1)
    i1 = np.clip(i0 + 1, 0, n - 1)
    f = np.clip(t - i0, 0, 1)
    linhas = img[i0] * (1 - f)[:, None] + img[i1] * f[:, None]
    return linhas[:, i0] * (1 - f)[None, :] + linhas[:, i1] * f[None, :]


def material_recorte(nome, img, limite, aspereza=0.8):
    """Material com recorte por alpha: mostra só os pixels com alfa > limite (shell fur)."""
    mat = bpy.data.materials.new(nome)
    mat.use_nodes = True
    nos = mat.node_tree.nodes
    bsdf = nos.get("Principled BSDF")
    tex = nos.new("ShaderNodeTexImage")
    tex.image = img
    maior = nos.new("ShaderNodeMath")
    maior.operation = "GREATER_THAN"
    maior.inputs[1].default_value = limite
    mat.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    mat.node_tree.links.new(tex.outputs["Alpha"], maior.inputs[0])
    mat.node_tree.links.new(maior.outputs[0], bsdf.inputs["Alpha"])
    bsdf.inputs["Roughness"].default_value = aspereza
    if "Specular IOR Level" in bsdf.inputs:
        bsdf.inputs["Specular IOR Level"].default_value = 0.25
    if hasattr(mat, "surface_render_method"):
        mat.surface_render_method = "DITHERED"
    mat["limite_alfa"] = limite
    return mat


def desenhar_fios(dens, n, comp_px, ang_mapa, rng, altura=None):
    """Fios curtos no espaço da textura. dens: matriz 0..1 (probabilidade de nascer um fio);
    ang_mapa: ângulo (rad) do fio em cada pixel; altura(dens_no_pixel) -> "altura" 0..1 do fio.
    Devolve (h, tom): h = maior altura de fio em cada pixel (0 = sem fio), tom = brilho do fio."""
    tam = dens.shape[0]
    cand = np.argwhere(dens > 0.02)
    prob = dens[cand[:, 0], cand[:, 1]].astype(np.float64)
    escolha = rng.choice(len(cand), size=n, p=prob / prob.sum())
    yy, xx = cand[escolha, 0], cand[escolha, 1]
    y0 = yy + rng.random(n)
    x0 = xx + rng.random(n)
    ang = ang_mapa[yy, xx] + rng.normal(0, 0.35, n)
    comp = rng.uniform(*comp_px, n)
    d = dens[yy, xx]
    h_fio = altura(d) if altura else np.ones(n)
    tom = rng.uniform(0.5, 1.7, n)
    h = np.zeros((tam, tam), np.float32)
    cor = np.zeros((tam, tam), np.float32)
    ordem = np.argsort(h_fio)          # os mais altos por cima
    x0, y0, ang, comp, h_fio, tom = (a[ordem] for a in (x0, y0, ang, comp, h_fio, tom))
    for t in np.linspace(0, 1, int(comp_px[1]) * 2):
        xi = np.clip((x0 + np.cos(ang) * comp * t).astype(int), 0, tam - 1)
        yi = np.clip((y0 + np.sin(ang) * comp * t).astype(int), 0, tam - 1)
        h[yi, xi] = np.maximum(h[yi, xi], h_fio * (1.0 - 0.25 * t))
        cor[yi, xi] = tom * (1.1 - 0.3 * t)
    return h, cor


def desfocar(img, raio):
    """Desfoque aproximadamente gaussiano (3 passadas de caixa) numa matriz 2D."""
    if raio < 1:
        return img
    out = img.astype(np.float32)
    for _ in range(3):
        for eixo in (0, 1):
            pad = [(0, 0), (0, 0)]
            pad[eixo] = (raio + 1, raio)
            c = np.cumsum(np.pad(out, pad, mode="edge"), axis=eixo)
            if eixo == 0:
                out = (c[2 * raio + 1:, :] - c[:-2 * raio - 1, :]) / (2 * raio + 1)
            else:
                out = (c[:, 2 * raio + 1:] - c[:, :-2 * raio - 1]) / (2 * raio + 1)
    return out


def rasterizar_uv(obj, valor, tamanho, so_faces=None):
    """Pinta no espaço UV (tamanho x tamanho, linha 0 = v 0) o valor por vértice, interpolado
    nos triângulos. so_faces: conjunto de índices de polígonos a considerar (opcional)."""
    me = obj.data
    me.calc_loop_triangles()
    uv = me.uv_layers.active.data
    img = np.zeros((tamanho, tamanho), np.float32)
    for t in me.loop_triangles:
        if so_faces is not None and t.polygon_index not in so_faces:
            continue
        w = np.array([valor[i] for i in t.vertices], np.float32)
        if w.max() <= 0:
            continue
        p = np.array([uv[l].uv[:] for l in t.loops], np.float32) * tamanho
        x0, y0 = np.floor(p.min(0)).astype(int)
        x1, y1 = np.ceil(p.max(0)).astype(int)
        x0, y0 = max(x0, 0), max(y0, 0)
        x1, y1 = min(x1, tamanho - 1), min(y1, tamanho - 1)
        if x1 < x0 or y1 < y0:
            continue
        xs, ys = np.meshgrid(np.arange(x0, x1 + 1) + 0.5, np.arange(y0, y1 + 1) + 0.5)
        (ax, ay), (bx, by), (cx, cy) = p
        den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy)
        if abs(den) < 1e-9:
            continue
        l0 = ((by - cy) * (xs - cx) + (cx - bx) * (ys - cy)) / den
        l1 = ((cy - ay) * (xs - cx) + (ax - cx) * (ys - cy)) / den
        l2 = 1 - l0 - l1
        tol = -1.0 / max(abs(den), 1) ** 0.5   # engorda 1 px para não deixar fresta
        dentro = (l0 >= tol) & (l1 >= tol) & (l2 >= tol)
        val = np.clip(l0 * w[0] + l1 * w[1] + l2 * w[2], 0, 1)
        bloco = img[y0:y1 + 1, x0:x1 + 1]
        np.maximum(bloco, np.where(dentro, val, 0), out=bloco)
    return img


def pixels(img):
    w, h = img.size
    return np.array(img.pixels[:], dtype=np.float32).reshape(h, w, 4)


def gravar_pixels(img, px):
    img.pixels[:] = np.clip(px, 0, 1).ravel()
    img.update()


# ---------------------------------------------------------------- humano

def altura_da_pele(base):
    corpo = indices_do_grupo(base, "body") or range(len(base.data.vertices))  # depois da limpeza é tudo pele
    co = coords_da_mistura(base)
    zs = [co[i].z for i in corpo]
    return max(zs) - min(zs)


def criar_humano(dados):
    """Cria o humano, ajustando o macro "height" até a pele medir ALTURA_ALVO."""
    alt_macro, anterior = MACROS["height"], None
    for tentativa in range(5):
        limpar_cena()
        macros = dict(MACROS, race=dict(MACROS["race"]), height=alt_macro)
        base = HumanService.create_human(mask_helpers=True, detailed_helpers=True,
                                         extra_vertex_groups=True, feet_on_ground=True,
                                         scale=0.1, macro_detail_dict=macros)
        raiz_alvos = LocationService.get_mpfb_data("targets")
        for alvo, peso in CORPO_ALVOS.items():
            caminho = os.path.join(raiz_alvos, alvo + ".target.gz")
            if not os.path.isfile(caminho):
                raise RuntimeError("alvo não encontrado: " + caminho)
            TargetService.load_target(base, caminho, weight=peso, name=os.path.basename(alvo))
        h = altura_da_pele(base)
        log("altura com height=%.3f: %.3f m" % (alt_macro, h))
        if abs(h - ALTURA_ALVO) < 0.006:
            break
        inclinacao = 0.35 if anterior is None else (h - anterior[1]) / (alt_macro - anterior[0])
        if inclinacao < 0.05:      # o macro não é linear perto de 0,5; evita passo gigante
            inclinacao = 0.35
        anterior = (alt_macro, h)
        alt_macro = min(1.0, max(0.0, alt_macro + (ALTURA_ALVO - h) / inclinacao))
    MACROS["height"] = alt_macro

    rig = HumanService.add_builtin_rig(base, RIG)
    HumanService.set_character_skin(os.path.join(dados, PELE), base, skin_type="GAMEENGINE",
                                    material_instances=False)
    filhos = {}
    for arquivo, tipo, nome in ASSETS:
        obj = HumanService.add_mhclo_asset(os.path.join(dados, arquivo), base, asset_type=tipo,
                                           subdiv_levels=0, material_type="GAMEENGINE")
        filhos[nome] = (obj, os.path.join(dados, arquivo))
    return base, rig, filhos


def capturar_deltas(base):
    """Calcula os deslocamentos de cada shape key desejada, relativos à forma atual."""
    ref = coords_da_mistura(base)
    deltas = {}

    for nome, mudancas in FORMAS_DO_CORPO.items():
        antigos = {k: HumanObjectProperties.get_value(k, entity_reference=base) for k in mudancas}
        for k, v in mudancas.items():
            HumanObjectProperties.set_value(k, v, entity_reference=base)
        TargetService.reapply_macro_details(base)
        co = coords_da_mistura(base)
        for k, v in antigos.items():
            HumanObjectProperties.set_value(k, v, entity_reference=base)
        TargetService.reapply_macro_details(base)
        deltas[nome] = [c - r for c, r in zip(co, ref)]

    for nome, unidades in EXPRESSOES.items():
        criadas = []
        for unidade, peso in unidades.items():
            for caminho, peso_raca in unidades_por_raca(unidade):
                if peso_raca > 0 and os.path.isfile(caminho):
                    sk = TargetService.load_target(base, caminho, weight=peso * peso_raca,
                                                   name="_expr_%d" % len(criadas))
                    criadas.append(sk.name)
        co = coords_da_mistura(base)
        for n in criadas:
            base.shape_key_remove(base.data.shape_keys.key_blocks[n])
        deltas[nome] = [c - r for c, r in zip(co, ref)]

    volta = coords_da_mistura(base)
    erro = max((a - b).length for a, b in zip(volta, ref))
    log("erro ao restaurar forma base: %.6f" % erro)
    return deltas


def aplicar_shape_keys(base, filhos, deltas):
    TargetService.bake_targets(base)
    base.shape_key_add(name="Basis", from_mix=False)
    for nome, d in deltas.items():
        sk = base.shape_key_add(name=nome, from_mix=False)
        for i, v in enumerate(d):
            sk.data[i].co = base.data.vertices[i].co + v
        sk.value = 0.0

    for nome_filho, (obj, arquivo) in filhos.items():
        mhclo = Mhclo()
        mhclo.load(arquivo)
        criou_basis = False
        for nome, d in deltas.items():
            mudancas = []
            for vi, mapa in mhclo.verts.items():
                if vi >= len(obj.data.vertices):
                    continue
                v0, v1, v2 = mapa["verts"]
                w0, w1, w2 = mapa["weights"]
                off = d[v0] * w0 + d[v1] * w1 + d[v2] * w2
                if off.length > 1e-5:
                    mudancas.append((vi, off))
            if not mudancas:
                continue
            if not criou_basis and not obj.data.shape_keys:
                obj.shape_key_add(name="Basis", from_mix=False)
                criou_basis = True
            sk = obj.shape_key_add(name=nome, from_mix=False)
            for vi, off in mudancas:
                sk.data[vi].co = obj.data.vertices[vi].co + off
            sk.value = 0.0


# ---------------------------------------------------------------- ajustes de forma

def referencias_da_cabeca(base, rig):
    co = np.array([v.co[:] for v in base.data.vertices])
    labios = indices_do_grupo(base, "lips")
    orelhas = indices_do_grupo(base, "ears")
    L, O = co[labios], co[orelhas]
    olho_e = rig.matrix_world @ rig.data.bones["mixamorig:LeftEye"].head_local
    olho_d = rig.matrix_world @ rig.data.bones["mixamorig:RightEye"].head_local
    corpo = indices_do_grupo(base, "body")
    return {
        "co": co,
        "labios": labios,
        "labios_centro": (L.min(0) + L.max(0)) / 2,
        "labios_meia": (L.max(0) - L.min(0)) / 2,
        "orelha_min": O.min(0), "orelha_max": O.max(0),
        "orelha_ax_min": float(np.abs(O[:, 0]).min()),
        "olhos": (olho_e, olho_d),
        "topo": co[corpo, 2].max(),
    }


def pesos_da_barba(ref):
    """Densidade 0..1 por vértice do corpo: onde há barba (maxilar, queixo, bigode, costeletas).
    Bordas em degradê (bochecha e pescoço); o bigode se liga à barba pelos cantos da boca:
    só o vermelho dos lábios (uma elipse) fica sem barba."""
    co = ref["co"]
    x, y, z = co[:, 0], co[:, 1], co[:, 2]
    ax = np.abs(x)
    lx, ly, lz = ref["labios_centro"]
    mx, _, mz = ref["labios_meia"]
    ruido = Ruido3D(0.018, SEMENTE + 1)(co)
    # limite de cima: bigode logo acima do lábio, subindo pelas bochechas até a costeleta
    topo = lz + np.interp(ax, [a for a, _ in BARBA_TOPO], [h for _, h in BARBA_TOPO])
    largura_topo = np.interp(ax, [0.0, 0.03, 0.05, 0.09], [0.005, 0.008, BARBA_BORDA_TOPO,
                                                            BARBA_BORDA_TOPO])
    # limite de baixo: do ângulo da mandíbula até um pouco abaixo do queixo
    baixo = np.interp(y, [ly - 0.03, ly, ly + 0.04, ly + 0.08, ly + 0.10, ly + 0.2],
                      [lz - 0.050, lz - 0.054, lz - 0.054, lz - 0.038, lz - 0.014, lz + 0.03])
    # limite de trás: frente da orelha (a costeleta encontra o cabelo)
    tras = np.interp(z, [lz - 0.06, lz, lz + 0.04, lz + 0.09],
                     [ly + 0.075, ly + 0.095, ly + 0.100, ly + 0.095])

    w = (rampa(topo - z + 0.004 * ruido, largura_topo)
         * rampa(z - baixo + 0.005 * ruido, BARBA_BORDA_PESCOCO)
         * rampa(tras - y, 0.010))
    # boca: elipse do vermelho dos lábios (só na frente do rosto)
    e = np.sqrt((x - lx) ** 2 / (mx + 0.0015) ** 2 + (z - lz) ** 2 / (mz + 0.0006) ** 2)
    frente = y < ly + 0.02
    w = np.where(frente, w * rampa(e - 1.04, 0.14), w)
    w[ax > 0.11] = 0.0
    return np.clip(w, 0, 1)


def reforcar_trapezio(base, rig):
    """Sobe a pele entre o pescoço e o ombro (trapézio), só nas faces voltadas para cima."""
    pescoco = rig.matrix_world @ rig.data.bones["mixamorig:Neck"].head_local
    ombro = rig.matrix_world @ rig.data.bones["mixamorig:LeftArm"].head_local
    x0, x1 = 0.035, abs(ombro.x) * 0.95
    co = coords_da_mistura(base)
    desloc = {}
    for v in base.data.vertices:
        p = co[v.index]
        ax = abs(p.x)
        if not (x0 < ax < x1) or not (ombro.z - 0.03 < p.z < pescoco.z + 0.07):
            continue
        n = v.normal
        if n.z < 0.25:
            continue
        f = math.sin(math.pi * (ax - x0) / (x1 - x0)) ** 0.8
        alto = min(1.0, max(0.0, (p.z - (ombro.z - 0.03)) / 0.04))
        k = TRAPEZIO_ALTURA * f * min(1.0, (n.z - 0.25) / 0.35) * alto
        if k > 1e-5:
            desloc[v.index] = Vector((0, 0.15 * k, k))
    mover_vertices(base, desloc)
    log("trapézio: %d vértices" % len(desloc))


def justar_camiseta(camiseta, bvh, rig):
    """Camiseta justa: puxa o tecido para perto da pele (mais nas mangas) e empurra para fora
    o que tiver ficado para dentro da pele depois dos músculos."""
    ombro_x = abs((rig.matrix_world @ rig.data.bones["mixamorig:LeftArm"].head_local).x)
    desloc = {}
    for v in camiseta.data.vertices:
        loc, n, _, _ = bvh.find_nearest(v.co)
        if loc is None:
            continue
        d = (v.co - loc).dot(n)
        k = float(rampa(abs(v.co.x) - ombro_x + 0.01, 0.05))
        aperto = CAMISETA_APERTO_TRONCO + (CAMISETA_APERTO_MANGA - CAMISETA_APERTO_TRONCO) * k
        if d < CAMISETA_FOLGA:
            novo = CAMISETA_FOLGA
        else:
            novo = CAMISETA_FOLGA + (d - CAMISETA_FOLGA) * (1 - aperto)
        if abs(novo - d) > 1e-5:
            desloc[v.index] = n * (novo - d)
    mover_vertices(camiseta, desloc)
    log("camiseta: %d vértices ajustados" % len(desloc))


def guardar_pesos_da_barba(base, w):
    g = base.vertex_groups.new(name="_barba")
    for i, p in enumerate(w):
        if p > 0.0:
            g.add([i], float(p), "REPLACE")


def afunilar_calca(roupa, base, bvh, rig):
    """Deixa a perna da calça justa (tipo jogger), puxando para o eixo da perna."""
    co = np.array([v.co[:] for v in base.data.vertices])
    pele = co[indices_do_grupo(base, "body")]
    joelho = (rig.matrix_world @ rig.data.bones["mixamorig:LeftLeg"].head_local).z
    alturas = [a for a, _ in CALCA_AFUNILAR]
    escalas = [e for _, e in CALCA_AFUNILAR]
    desloc = {}
    for v in roupa.data.vertices:
        z = v.co.z
        if z > joelho + 0.03:
            continue
        lado = pele[(np.abs(pele[:, 2] - z) < 0.012) & (np.sign(pele[:, 0]) == np.sign(v.co.x))
                    & (pele[:, 2] < joelho + 0.1)]
        if len(lado) < 4:
            continue
        cx, cy = lado[:, 0].mean(), lado[:, 1].mean()
        s = float(np.interp(z, alturas, escalas))
        novo = Vector((cx + (v.co.x - cx) * s, cy + (v.co.y - cy) * s, z))
        loc, n, _, _ = bvh.find_nearest(novo)
        if loc is not None:
            d = (novo - loc).dot(n)
            if d < 0.004:
                novo += n * (0.004 - d)
        desloc[v.index] = novo - v.co
    mover_vertices(roupa, desloc)
    log("calça: %d vértices afunilados" % len(desloc))


def limpar_corpo(base, rig, manter=("_barba",)):
    """Apaga helpers/juntas e a pele escondida debaixo da roupa; deixa só grupos de ossos."""
    grupos = {g.index: g.name for g in base.vertex_groups}
    manter_idx = next(i for i, n in grupos.items() if n == "body")
    apagar_idx = {i for i, n in grupos.items() if n.startswith("Delete.")}

    bm = bmesh.new()
    bm.from_mesh(base.data)
    deform = bm.verts.layers.deform.verify()
    remover = []
    for v in bm.verts:
        dv = v[deform]
        if dv.get(manter_idx, 0.0) <= 0.0 or any(dv.get(i, 0.0) > 0.0 for i in apagar_idx):
            remover.append(v)
    bmesh.ops.delete(bm, geom=remover, context="VERTS")
    bm.to_mesh(base.data)
    bm.free()
    base.data.update()

    for m in list(base.modifiers):
        if m.type != "ARMATURE":
            base.modifiers.remove(m)

    ossos = {b.name for b in rig.data.bones}
    for o in [base] + [c for c in rig.children_recursive if c.type == "MESH"]:
        for g in list(o.vertex_groups):
            if g.name not in ossos and g.name not in manter:
                o.vertex_groups.remove(g)
        for m in list(o.modifiers):
            if m.type != "ARMATURE":
                o.modifiers.remove(m)
    log("corpo: %d vértices depois da limpeza" % len(base.data.vertices))


def separar_roupa(roupa):
    """Separa camiseta e calça (vêm num arquivo só) pelas partes soltas."""
    bpy.ops.object.select_all(action="DESELECT")
    bpy.context.view_layer.objects.active = roupa
    roupa.select_set(True)
    me = roupa.data
    bm = bmesh.new()
    bm.from_mesh(me)
    bm.faces.ensure_lookup_table()
    visto, partes = set(), []
    for f in bm.faces:
        if f.index in visto:
            continue
        pilha, parte = [f], []
        visto.add(f.index)
        while pilha:
            x = pilha.pop()
            parte.append(x.index)
            for e in x.edges:
                for g in e.link_faces:
                    if g.index not in visto:
                        visto.add(g.index)
                        pilha.append(g)
        zs = [v.co.z for i in parte for v in bm.faces[i].verts]
        partes.append((parte, sum(zs) / len(zs)))
    bm.free()
    log("roupa: %d partes soltas" % len(partes))
    if len(partes) < 2:
        return {"Roupa": roupa}

    cintura = 0.98  # metros; partes com centro acima disso são da camiseta
    cima = {i for parte, z in partes if z > cintura for i in parte}
    if not cima or len(cima) == len(me.polygons):
        return {"Roupa": roupa}
    for p in me.polygons:
        p.select = p.index in cima
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.separate(type="SELECTED")
    bpy.ops.object.mode_set(mode="OBJECT")
    nova = [o for o in bpy.context.selected_objects if o != roupa][0]
    return {"Camiseta": nova, "Calca": roupa}


# ---------------------------------------------------------------- barba

def ler_grupo(obj, nome):
    g = obj.vertex_groups[nome]
    peso = np.zeros(len(obj.data.vertices), np.float32)
    for v in obj.data.vertices:
        for x in v.groups:
            if x.group == g.index:
                peso[v.index] = x.weight
    return peso


def copiar_faces(base, nome, manter):
    """Cópia do corpo só com as faces em `manter` (índices de polígonos). Herda ossos,
    shape keys e UV, então acompanha sorriso, olhos cansados e variações de corpo."""
    obj = base.copy()
    obj.data = base.data.copy()
    for col in base.users_collection:
        col.objects.link(obj)
    obj.name = obj.data.name = nome
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bm.faces.ensure_lookup_table()
    fora = [f for f in bm.faces if f.index not in manter]
    bmesh.ops.delete(bm, geom=fora, context="FACES_ONLY")
    soltos = [v for v in bm.verts if not v.link_faces]
    bmesh.ops.delete(bm, geom=soltos, context="VERTS")
    bm.to_mesh(obj.data)
    bm.free()
    obj.data.update()
    return obj


def juntar(objs, nome):
    principal = objs[0]
    with bpy.context.temp_override(active_object=principal, selected_editable_objects=objs,
                                   selected_objects=objs, object=principal):
        bpy.ops.object.join()
    principal.name = principal.data.name = nome
    return principal


def angulo_para_baixo(base, faces, tam):
    """Ângulo (no espaço da textura) que aponta "para baixo" no corpo, pixel a pixel."""
    zs = np.array([v.co.z for v in base.data.vertices], np.float32)
    zn = (zs - zs.min() + 0.01) / (zs.max() - zs.min() + 0.01)
    mapa_z = desfocar(rasterizar_uv(base, zn, tam, so_faces=faces), 4)
    gy, gx = np.gradient(mapa_z)
    return np.arctan2(-gy, -gx)


def criar_barba(base):
    """Barba = pintura na pele (degradê) + 2 cascas de fios (shell fur) por cima.
    Devolve o objeto e a camada que vai ser pintada na pele."""
    peso = ler_grupo(base, "_barba")
    tam = MAX_TEXTURA
    rng = np.random.default_rng(SEMENTE)
    faces_barba = {p.index for p in base.data.polygons if max(peso[i] for i in p.vertices) > 0}
    mascara = rasterizar_uv(base, peso, tam, so_faces=faces_barba)
    dens = np.clip(desfocar(mascara, 2), 0, 1)
    ang = angulo_para_baixo(base, faces_barba, tam)

    # textura das cascas: alfa = "altura" do fio; fundo cheio no centro da barba
    h, tom = desenhar_fios(dens ** 1.4, BARBA_FIOS, BARBA_FIO_PX, ang, rng,
                           altura=lambda d: rng.uniform(0.32, 1.0, len(d)) * (0.40 + 0.60 * d))
    grao = desfocar(rng.random((tam, tam)).astype(np.float32), 1)
    fundo = 0.50 * suave((dens - 0.55) / 0.40) * (0.80 + 0.4 * grao)
    alfa = np.maximum(h, fundo)
    brilho = np.where(h >= fundo, tom, 0.70)
    px = np.zeros((tam, tam, 4), np.float32)
    for c in range(3):
        px[:, :, c] = BARBA_COR[c] * brilho
    px[:, :, 3] = alfa
    img = bpy.data.images.new("barba_diffuse", tam, tam, alpha=True)
    gravar_pixels(img, px)
    img.filepath_raw = os.path.join(PASTA_FBX, "barba_diffuse.png")
    img.file_format = "PNG"

    cascas = []
    for nome_mat, e_min, e_max, limite, p_min in BARBA_CASCAS:
        manter = {p.index for p in base.data.polygons if max(peso[i] for i in p.vertices) > p_min}
        casca = copiar_faces(base, "Barba_" + nome_mat, manter)
        pc = ler_grupo(casca, "_barba")
        desloc = {v.index: v.normal.copy() * (e_min + (e_max - e_min) * float(suave(pc[v.index] * 1.2)))
                  for v in casca.data.vertices}
        mover_vertices(casca, desloc)
        casca.vertex_groups.remove(casca.vertex_groups["_barba"])
        casca.data.materials.clear()
        casca.data.materials.append(material_recorte(nome_mat, img, limite, 0.8))
        cascas.append(casca)
        log("barba: casca %s com %d faces" % (nome_mat, len(casca.data.polygons)))
    barba = juntar(cascas, "Barba")
    base.vertex_groups.remove(base.vertex_groups["_barba"])

    # pintura na pele: sombra com degradê + fios curtos (o que se vê entre os fios das cascas)
    suave_pele = np.clip(desfocar(mascara, 6) * 1.15, 0, 1)
    fios_pele, _ = desenhar_fios(np.clip(desfocar(mascara, 3), 0, 1), 60000, (4, 9), ang, rng)
    cobertura = np.maximum(0.70 * suave(suave_pele), 0.95 * (fios_pele > 0) * suave_pele ** 0.6)
    return barba, (BARBA_NA_PELE * cobertura, np.array(BARBA_COR, np.float32) * 1.25)


# ---------------------------------------------------------------- cabelo

def campos_do_cabelo(P, ref, ruido):
    """Para pontos P (N x 3) na pele da cabeça: (densidade 0..1, comprimento em m)."""
    omin, omax = ref["orelha_min"], ref["orelha_max"]
    e_meio, e_topo, e_baixo = (omin[2] + omax[2]) / 2, omax[2], omin[2]
    z_olho = (ref["olhos"][0].z + ref["olhos"][1].z) / 2
    cy = (omin[1] + omax[1]) / 2
    x, y, z = P[:, 0], P[:, 1], P[:, 2]
    alfa = np.degrees(np.arctan2(np.abs(x), -(y - cy)))      # 0 = frente, 180 = nuca
    r = ruido(P)
    # linha do cabelo (altura mínima) pelo ângulo em volta da cabeça
    linha = np.interp(alfa, [0, 20, 35, 50, 62, 68, 80, 84, 100, 125, 150, 180],
                      [z_olho + 0.068, z_olho + 0.071, z_olho + 0.065, z_olho + 0.050,
                       z_olho + 0.030, e_meio, e_meio, e_topo + 0.006, e_topo + 0.006,
                       e_meio, e_baixo - 0.010, e_baixo - 0.025]) + 0.003 * r
    largura = np.interp(alfa, [0, 55, 90, 180], [0.007, 0.010, 0.016, 0.026])
    dens = rampa(z - linha, largura)
    # degradê das laterais e nuca: fica ralo embaixo
    lado = rampa(alfa - 58, 10)
    dens *= 1 - lado * (1 - (0.12 + 0.88 * rampa(z - (e_topo + 0.016), 0.040)))
    # orelhas e pescoço
    orelha = ((np.abs(x) > ref["orelha_ax_min"] - 0.002) & (y > omin[1] - 0.004)
              & (y < omax[1] + 0.004) & (z > omin[2] - 0.004) & (z < omax[2] + 0.004))
    dens[orelha] = 0
    dens[z < e_baixo - 0.05] = 0
    rel = (z - e_meio) / (ref["topo"] - e_meio)
    comp = np.interp(rel, [a for a, _ in CABELO_ALTURA], [c for _, c in CABELO_ALTURA])
    comp *= 0.40 + 0.60 * rampa(z - linha - 0.012, 0.022)       # mais curto na linha da testa
    comp *= 0.78 + 0.22 * r                                       # silhueta irregular no alto
    return np.clip(dens, 0, 1), np.maximum(comp, 0)


def raios_da_cabeca(bvh, centro, n):
    """Grade n x n no disco de UV -> pontos na pele por raios a partir do centro da cabeça.
    Devolve (gx, gy, P, N, poligono, valido)."""
    g = np.linspace(-1, 1, n)
    gx, gy = np.meshgrid(g, g)
    P = np.zeros((n, n, 3))
    N = np.zeros((n, n, 3))
    poli = -np.ones((n, n), int)
    valido = np.zeros((n, n), bool)
    for i in range(n):
        for j in range(n):
            R = math.hypot(gx[i, j], gy[i, j])
            if R > 1.0 + 1e-9:
                continue
            th, ph = R * CABELO_THETA_MAX, math.atan2(gy[i, j], gx[i, j])
            d = Vector((math.sin(th) * math.cos(ph), -math.sin(th) * math.sin(ph), math.cos(th)))
            loc, nrm, idx, _ = bvh.ray_cast(centro, d, 0.3)
            if loc is None:
                continue
            if nrm.dot(d) < 0:
                nrm = -nrm
            nrm = (nrm + d * 0.6).normalized()
            P[i, j], N[i, j], poli[i, j], valido[i, j] = loc[:], nrm[:], idx, True
    return gx, gy, P, N, poli, valido


def textura_dos_cachos(dens, comp, px_por_m, rng):
    """Alfa = altura dos cachos, RGB = brilho. Tufos em domo dão o relevo (silhueta irregular)
    e fios encaracolados (espirais curtas com ruído) dão a textura de cachos e o brilho."""
    tam = dens.shape[0]
    area = (dens > 0.05).sum() / px_por_m ** 2
    cand = np.argwhere(dens > 0.05)
    prob = (dens[cand[:, 0], cand[:, 1]] ** 2.0).astype(np.float64)
    prob /= prob.sum()
    # relevo: tufos em domo
    n_tufos = int(area / (math.pi * 0.0058 ** 2) * 1.3)
    tufo = np.zeros((tam, tam), np.float32)
    for (cy_, cx_) in cand[rng.choice(len(cand), n_tufos, p=prob)]:
        R = rng.uniform(*CABELO_CACHO_RAIO) * px_por_m
        hc = rng.uniform(0.55, 1.0) * (0.55 + 0.45 * min(1.0, comp[cy_, cx_] / 0.012))
        r0 = int(math.ceil(R))
        y0, y1 = max(cy_ - r0, 0), min(cy_ + r0 + 1, tam)
        x0, x1 = max(cx_ - r0, 0), min(cx_ + r0 + 1, tam)
        yy, xx = np.mgrid[y0:y1, x0:x1]
        q = 1 - ((yy - cy_) ** 2 + (xx - cx_) ** 2) / (R * R)
        domo = (hc * np.sqrt(np.clip(q, 0, 1))).astype(np.float32)
        np.maximum(tufo[y0:y1, x0:x1], domo, out=tufo[y0:y1, x0:x1])
    # fios encaracolados: caminho com curvatura ~1/raio (1 a 2 voltas), com ruído
    passos = 36
    r_med = 0.5 * sum(CABELO_ANEL_RAIO) * px_por_m
    n_fios = int((dens > 0.05).sum() * 3.2 / (2 * math.pi * r_med * 1.4 * 2.2))
    prob_f = (dens[cand[:, 0], cand[:, 1]] ** 1.6).astype(np.float64)
    pos = cand[rng.choice(len(cand), n_fios, p=prob_f / prob_f.sum())].astype(np.float64) + rng.random((n_fios, 2))
    raio = rng.uniform(*CABELO_ANEL_RAIO, n_fios) * px_por_m
    voltas = rng.uniform(0.7, 1.8, n_fios)
    sentido = rng.choice([-1.0, 1.0], n_fios)
    ds = 2 * math.pi * raio * voltas / passos
    rumo = rng.uniform(0, 2 * math.pi, n_fios)
    pyx = np.zeros((n_fios, passos + 1, 2))
    rumos = np.zeros((n_fios, passos + 1))
    pyx[:, 0] = pos
    rumos[:, 0] = rumo
    for k in range(passos):
        rumo = rumo + sentido * ds / raio * rng.uniform(0.6, 1.4, n_fios) + rng.normal(0, 0.12, n_fios)
        pos = pos + ds[:, None] * np.stack([np.sin(rumo), np.cos(rumo)], 1)
        pyx[:, k + 1], rumos[:, k + 1] = pos, rumo
    y0i = np.clip(pyx[:, 0, 0].astype(int), 0, tam - 1)
    x0i = np.clip(pyx[:, 0, 1].astype(int), 0, tam - 1)
    d0 = dens[y0i, x0i]
    h = np.clip(np.maximum(tufo[y0i, x0i] * rng.uniform(0.70, 1.08, n_fios), 0.16 + 0.22 * d0)
                * np.sqrt(d0), 0, 1)
    tom = rng.uniform(0.75, 1.25, n_fios)
    luz = 0.6                                                  # direção "da luz" na textura
    ordem = np.argsort(h)
    pyx, rumos, h, tom = pyx[ordem], rumos[ordem], h[ordem], tom[ordem]
    fio = np.zeros((tam, tam), np.float32)
    cor_fio = np.zeros((tam, tam), np.float32)
    realce = tom[:, None] * (0.55 + 1.7 * np.abs(np.sin(rumos - luz)) ** 6)
    hh = np.broadcast_to(h[:, None], rumos.shape)
    for dy, dx in ((0.0, 0.0), (0.9, 0.0), (0.0, 0.9), (0.9, 0.9)):
        yi = np.clip((pyx[:, :, 0] + dy).astype(int), 0, tam - 1)
        xi = np.clip((pyx[:, :, 1] + dx).astype(int), 0, tam - 1)
        fio[yi, xi] = hh
        cor_fio[yi, xi] = realce
    base = np.maximum(0.22 * dens ** 1.5, 0.80 * tufo * np.clip(dens, 0, 1) ** 1.5)
    alfa = np.maximum(base, fio)
    luz_px = np.where(fio >= base, cor_fio, 0.42 + 0.3 * tufo)
    luz_px = np.where(fio > 0, luz_px, 0.42 + 0.3 * tufo)
    log("cabelo: %d tufos, %d fios encaracolados" % (n_tufos, n_fios))
    return alfa, luz_px


def criar_cabelo(base, ref):
    """Cabelo de cachos curtos: 3 cascas (grade no disco de UV, por raios) + pintura no couro
    cabeludo. Devolve o objeto e a camada pintada na pele."""
    rng = np.random.default_rng(SEMENTE + 2)
    ruido = Ruido3D(0.045, SEMENTE + 3)
    omin, omax = ref["orelha_min"], ref["orelha_max"]
    centro = Vector((0.0, (omin[1] + omax[1]) / 2, (omin[2] + omax[2]) / 2 + 0.025))
    bvh = bvh_atual(base)

    # ---- geometria: grade de células, só onde tem cabelo
    n = CABELO_GRADE + 1
    gx, gy, P, N, poli, valido = raios_da_cabeca(bvh, centro, n)
    dens, comp = np.zeros((n, n)), np.zeros((n, n))
    dens[valido], comp[valido] = campos_do_cabelo(P[valido], ref, ruido)
    verts, uvs, origem, faces, mats = [], [], [], [], []
    for k, (nome_mat, frac, limite) in enumerate(CABELO_CASCAS):
        idx = {}

        def vert(i, j):
            if (i, j) not in idx:
                off = 0.0010 + comp[i, j] * frac
                idx[(i, j)] = len(verts)
                verts.append(Vector(P[i, j]) + Vector(N[i, j]) * off)
                uvs.append((0.5 + 0.5 * gx[i, j], 0.5 + 0.5 * gy[i, j]))
                origem.append((i, j))
            return idx[(i, j)]

        for i in range(n - 1):
            for j in range(n - 1):
                cel = [(i, j), (i, j + 1), (i + 1, j + 1), (i + 1, j)]
                if not all(valido[c] for c in cel):
                    continue
                dmax = max(dens[c] for c in cel)
                cmax = max(comp[c] * frac for c in cel)
                if dmax < 0.03 or (k > 0 and (dmax < 0.25 or cmax < 0.0018 * k)):
                    continue
                faces.append([vert(*c) for c in cel])
                mats.append(k)
    me = bpy.data.meshes.new("Cabelo")
    me.from_pydata([v[:] for v in verts], [], faces)
    me.update()
    camada = me.uv_layers.new(name="UVMap")
    for p in me.polygons:
        for li in p.loop_indices:
            camada.data[li].uv = uvs[me.loops[li].vertex_index]
    # normais para fora (a grade pode vir com a ordem trocada)
    for p in me.polygons:
        i, j = origem[p.vertices[0]]
        if p.normal.dot(Vector(N[i, j])) < 0:
            p.flip()
    me.update()
    cabelo = bpy.data.objects.new("Cabelo", me)
    for col in base.users_collection:
        col.objects.link(cabelo)

    # ---- ossos e shape keys: interpolados da face da pele onde o raio bateu
    bv = base.data.vertices
    chaves = list(base.data.shape_keys.key_blocks) if base.data.shape_keys else []
    grupos = {g.index: g.name for g in base.vertex_groups}
    pesos_por_vert = []
    for (i, j) in origem:
        pol = base.data.polygons[int(poli[i, j])]
        pts = [bv[a].co for a in pol.vertices]
        pesos_por_vert.append((list(pol.vertices), poly_3d_calc(pts, Vector(P[i, j]))))
    for vi, (vs, w) in enumerate(pesos_por_vert):
        acc = {}
        for a, wa in zip(vs, w):
            for g in bv[a].groups:
                acc[grupos[g.group]] = acc.get(grupos[g.group], 0.0) + wa * g.weight
        for nome, p in acc.items():
            if p > 1e-4:
                (cabelo.vertex_groups.get(nome) or cabelo.vertex_groups.new(name=nome)).add(
                    [vi], p, "REPLACE")
    if chaves:
        basis = chaves[0]
        cabelo.shape_key_add(name="Basis", from_mix=False)
        for kb in chaves[1:]:
            sk = cabelo.shape_key_add(name=kb.name, from_mix=False)
            mexeu = False
            for vi, (vs, w) in enumerate(pesos_por_vert):
                d = Vector((0, 0, 0))
                for a, wa in zip(vs, w):
                    d += (kb.data[a].co - basis.data[a].co) * wa
                if d.length > 1e-6:
                    sk.data[vi].co = me.vertices[vi].co + d
                    mexeu = True
            if not mexeu:
                cabelo.shape_key_remove(sk)
    cabelo.parent = base.parent
    mod = cabelo.modifiers.new("Armature", "ARMATURE")
    mod.object = base.parent

    # ---- textura: campos numa grade fina, ampliados para a textura
    nf = 161
    fx, fy, FP, _, _, fval = raios_da_cabeca(bvh, centro, nf)
    fd, fc = np.zeros((nf, nf)), np.zeros((nf, nf))
    fd[fval], fc[fval] = campos_do_cabelo(FP[fval], ref, ruido)
    passo = [np.linalg.norm(FP[i, j + 1] - FP[i, j]) for i in range(nf) for j in range(nf - 1)
             if fval[i, j] and fval[i, j + 1]]
    tam = MAX_TEXTURA
    px_por_m = (tam / (nf - 1)) / float(np.median(passo))
    # linha 0 da textura = v 0 = gy -1 (a grade já está nessa ordem)
    D = np.clip(ampliar(fd, tam), 0, 1).astype(np.float32)
    C = np.clip(ampliar(fc, tam), 0, None).astype(np.float32)
    alfa, luz = textura_dos_cachos(D, C, px_por_m, rng)
    px = np.zeros((tam, tam, 4), np.float32)
    for c in range(3):
        px[:, :, c] = CABELO_COR[c] * luz
    px[:, :, 3] = alfa
    img = bpy.data.images.new("cabelo_diffuse", tam, tam, alpha=True)
    gravar_pixels(img, px)
    img.filepath_raw = os.path.join(PASTA_FBX, "cabelo_diffuse.png")
    img.file_format = "PNG"
    for nome_mat, _, limite in CABELO_CASCAS:
        me.materials.append(material_recorte(nome_mat, img, limite, 0.7))
    for p, m in zip(me.polygons, mats):
        p.material_index = m
        p.use_smooth = True
    log("cabelo: %d faces nas cascas %s, %.0f px/m na textura" % (
        len(faces), [mats.count(k) for k in range(len(CABELO_CASCAS))], px_por_m))

    # ---- pintura no couro cabeludo (pele): escurece com degradê + pontinhos de fio
    co = np.array([v.co[:] for v in bv])
    dv, _ = campos_do_cabelo(co, ref, ruido)
    cabeca = {p.index for p in base.data.polygons if max(dv[i] for i in p.vertices) > 0}
    m = np.clip(desfocar(rasterizar_uv(base, dv, tam, so_faces=cabeca), 3), 0, 1)
    ang = rng.uniform(0, 2 * math.pi, (tam, tam)).astype(np.float32)
    fios, _ = desenhar_fios(m, 90000, (3, 7), ang, rng)
    cobertura = np.maximum(0.80 * suave(m), 0.95 * (fios > 0) * m ** 0.5)
    return cabelo, (CABELO_NA_PELE * cobertura, np.array(CABELO_COR, np.float32) * 1.5)


# ---------------------------------------------------------------- óculos

def tubo(bm, pontos, raios, cima, lados=6):
    """Tubo de seção elíptica ao longo de pontos. raios: [(r_na_direção_cima, r_lateral)]."""
    aneis = []
    for i, p in enumerate(pontos):
        a = pontos[max(i - 1, 0)]
        b = pontos[min(i + 1, len(pontos) - 1)]
        t = (b - a).normalized()
        b1 = (cima - t * cima.dot(t)).normalized()
        b2 = t.cross(b1)
        r1, r2 = raios[i]
        anel = []
        for k in range(lados):
            ang = 2 * math.pi * k / lados
            anel.append(bm.verts.new(p + b1 * (r1 * math.cos(ang)) + b2 * (r2 * math.sin(ang))))
        aneis.append(anel)
    faces = []
    for a, b in zip(aneis, aneis[1:]):
        for k in range(lados):
            faces.append(bm.faces.new((a[k], a[(k + 1) % lados], b[(k + 1) % lados], b[k])))
    faces.append(bm.faces.new(list(reversed(aneis[0]))))
    faces.append(bm.faces.new(aneis[-1]))
    return faces


def retangulo_arredondado(ang):
    """Ponto (u, v) no contorno da lente (superelipse) para o ângulo dado."""
    a, b = OCULOS_LENTE_LARGURA / 2, OCULOS_LENTE_ALTURA / 2
    e = 2.0 / OCULOS_EXPOENTE_CANTO
    c, s = math.cos(ang), math.sin(ang)
    return a * math.copysign(abs(c) ** e, c), b * math.copysign(abs(s) ** e, s)


def criar_oculos(base, rig, bvh, ref):
    olho_e, olho_d = ref["olhos"]
    z_olho = (olho_e.z + olho_d.z) / 2
    frente_olho = min(olho_e.y, olho_d.y) - 0.012          # córnea fica ~12 mm à frente do centro
    hit = bvh.ray_cast(Vector((0, -0.4, z_olho - 0.006)), Vector((0, 1, 0)))
    y_nariz = hit[0].y if hit[0] else frente_olho - 0.012
    y_lente = min(frente_olho - OCULOS_DISTANCIA_OLHO, y_nariz - 0.003)
    cx = OCULOS_PONTE / 2 + OCULOS_LENTE_LARGURA / 2
    zc = z_olho + OCULOS_ALTURA_LENTE
    curva = math.radians(OCULOS_CURVA)
    a, b = OCULOS_LENTE_LARGURA / 2, OCULOS_LENTE_ALTURA / 2
    log("óculos: olhos z=%.3f, frente do olho y=%.3f, nariz y=%.3f, lente y=%.3f"
        % (z_olho, frente_olho, y_nariz, y_lente))

    def ponto(s, u, v, dy=0.0):
        """s: lado (+1 = +x). u: distância para fora a partir do centro da lente; v: para cima."""
        x = s * (cx + u * math.cos(curva))
        y = y_lente + u * math.sin(curva) + 0.6 * u * u + dy
        return Vector((x, y, zc + v))

    bm = bmesh.new()
    dobradica = {}
    for s in (1, -1):
        if OCULOS_COM_LENTES:
            # lente: leque a partir do centro, um pouco atrás do aro
            centro = bm.verts.new(ponto(s, 0, 0, 0.0005))
            n = 24
            anel = [bm.verts.new(ponto(s, *retangulo_arredondado(2 * math.pi * i / n), dy=0.0005))
                    for i in range(n)]
            for i in range(n):
                f = bm.faces.new((centro, anel[i], anel[(i + 1) % n]))
                f.material_index = 1
        # aro só em cima (meia-armação): desce um pouco pelas laterais e afina nas pontas
        caminho, raios = [], []
        for i in range(23):
            ang = -0.35 + (math.pi + 0.70) * i / 22
            u, v = retangulo_arredondado(ang)
            caminho.append(ponto(s, u * 1.01, v * 1.03))
            k = max(0.0, math.sin(ang)) ** 0.5
            raios.append((0.0015, OCULOS_ARO_LADO + (OCULOS_ARO_TOPO - OCULOS_ARO_LADO) * k))
        tubo(bm, caminho, raios, Vector((0, -1, 0)))
        dobradica[s] = ponto(s, a + 0.002, b * 0.60, -0.001)

    # ponte entre as lentes
    ponte = []
    for i in range(9):
        t = i / 8
        x = -(OCULOS_PONTE / 2 + 0.002) + (OCULOS_PONTE + 0.004) * t
        ponte.append(Vector((x, y_lente - 0.0005, zc + b * 0.65 + 0.002 * math.sin(math.pi * t))))
    tubo(bm, ponte, [(0.0012, 0.0013)] * len(ponte), Vector((0, -1, 0)))

    # hastes: da dobradiça, rente à lateral da cabeça, até atrás da orelha
    omin, omax = ref["orelha_min"], ref["orelha_max"]
    z_orelha = omax[2] + 0.001
    y_fim = omin[1] + 0.022
    for s in (1, -1):
        h = dobradica[s]
        caminho = [h]
        n = 12
        for i in range(1, n + 1):
            t = i / n
            y = h.y + (y_fim - h.y) * t
            z = h.z + (z_orelha - h.z) * t
            hit = bvh.ray_cast(Vector((s * 0.3, y, z + 0.004)), Vector((-s, 0, 0)))
            x = abs(hit[0].x) + 0.0045 if hit[0] else abs(h.x)   # por fora do cabelo
            if t < 0.3:
                x = max(x, abs(h.x))
            caminho.append(Vector((s * x, y, z)))
        for dy, dz in ((0.009, -0.009), (0.015, -0.024)):
            y, z = y_fim + dy, z_orelha + dz
            hit = bvh.ray_cast(Vector((0, y, z)), Vector((s, 0, 0)))     # de dentro: ignora a orelha
            x = abs(hit[0].x) + 0.0035 if hit[0] else abs(caminho[-1].x)
            caminho.append(Vector((s * x, y, z)))
        tubo(bm, caminho, [(0.0024, 0.0010)] * len(caminho), Vector((0, 0, 1)))

    # normais: lentes para a frente; tubos para fora
    for f in bm.faces:
        if f.material_index == 1 and f.normal.y > 0:
            f.normal_flip()
    tubos = [f for f in bm.faces if f.material_index == 0]
    bmesh.ops.recalc_face_normals(bm, faces=tubos)

    me = bpy.data.meshes.new("Oculos")
    bm.to_mesh(me)
    bm.free()
    oculos = bpy.data.objects.new("Oculos", me)
    for col in base.users_collection:
        col.objects.link(oculos)

    # nada dentro da pele
    empurrados = 0
    for v in me.vertices:
        loc, nrm, _, _ = bvh.find_nearest(v.co)
        if loc is None or (v.co - loc).length > 0.02:
            continue
        d = (v.co - loc).dot(nrm)
        if d < OCULOS_FOLGA:
            v.co += nrm * (OCULOS_FOLGA - d)
            empurrados += 1
    log("óculos: %d vértices afastados da pele" % empurrados)

    armacao = bpy.data.materials.new("M_Oculos")
    armacao.use_nodes = True
    p = armacao.node_tree.nodes.get("Principled BSDF")
    p.inputs["Base Color"].default_value = (*OCULOS_COR, 1)
    p.inputs["Roughness"].default_value = 0.3
    armacao.diffuse_color = (*OCULOS_COR, 1)
    me.materials.append(armacao)
    if not OCULOS_COM_LENTES:
        for poly in me.polygons:
            poly.use_smooth = True
        g = oculos.vertex_groups.new(name="mixamorig:Head")
        g.add(list(range(len(me.vertices))), 1.0, "REPLACE")
        oculos.parent = rig
        mod = oculos.modifiers.new("Armature", "ARMATURE")
        mod.object = rig
        return oculos
    lente = bpy.data.materials.new("M_Oculos_Lente")
    lente.use_nodes = True
    p = lente.node_tree.nodes.get("Principled BSDF")
    p.inputs["Base Color"].default_value = (0.02, 0.02, 0.02, 1)
    p.inputs["Roughness"].default_value = 0.6
    p.inputs["Alpha"].default_value = OCULOS_LENTE_ALFA
    lente.diffuse_color = (0.02, 0.02, 0.02, OCULOS_LENTE_ALFA)
    if hasattr(lente, "surface_render_method"):
        lente.surface_render_method = "BLENDED"
    lente.use_backface_culling = False
    me.materials.append(lente)
    for poly in me.polygons:
        poly.use_smooth = poly.material_index == 0

    g = oculos.vertex_groups.new(name="mixamorig:Head")
    g.add(list(range(len(me.vertices))), 1.0, "REPLACE")
    oculos.parent = rig
    mod = oculos.modifiers.new("Armature", "ARMATURE")
    mod.object = rig
    return oculos


# ---------------------------------------------------------------- tênis

def mascara_da_sola(tenis, tamanho):
    """Máscara UV das faces da sola (perto do chão)."""
    zmin = min(v.co.z for v in tenis.data.vertices)
    sola = {p.index for p in tenis.data.polygons
            if max(tenis.data.vertices[i].co.z for i in p.vertices) < zmin + TENIS_SOLA_ALTURA}
    um = [1.0] * len(tenis.data.vertices)
    log("tênis: %d de %d faces na sola" % (len(sola), len(tenis.data.polygons)))
    return rasterizar_uv(tenis, um, tamanho, so_faces=sola)


# ---------------------------------------------------------------- texturas

def luminancia(px):
    return px[:, :, 0] * 0.299 + px[:, :, 1] * 0.587 + px[:, :, 2] * 0.114


def processar_pele(img, camadas):
    """Cor média da pele + camadas pintadas por cima: [(cobertura 0..1, cor RGB)]."""
    px = pixels(img)
    rgb = px[:, :, :3]
    lum = luminancia(px)
    valido = lum > 0.15
    media = rgb[valido].mean(axis=0)
    ganho = np.array(PELE_COR_MEDIA, np.float32) / media
    log("pele: média original %s, ganho %s" % (np.round(media, 3), np.round(ganho, 3)))
    rgb *= ganho
    for cobertura, cor in camadas:
        if cobertura.shape != lum.shape:
            raise RuntimeError("camada com tamanho diferente da textura da pele")
        k = np.clip(cobertura, 0, 1)[:, :, None]
        rgb[:] = rgb * (1 - k) + cor[None, None, :] * k
    gravar_pixels(img, px)


def processar_olhos(img):
    px = pixels(img)
    rgb = px[:, :, :3]
    sat = rgb.max(axis=2) - rgb.min(axis=2)
    k = np.clip((sat - 0.08) / 0.15, 0, 1)[:, :, None]
    rgb[:] = rgb * (1 - k) + rgb * np.array(OLHOS_GANHO, np.float32) * k
    gravar_pixels(img, px)


def tingir_com_alfa(img, cor, detalhe=0.6, reforco_alfa=1.0):
    """Pinta na cor dada mantendo alfa (reforçado) e um pouco do desenho original."""
    px = pixels(img)
    px[:, :, 3] = np.clip(px[:, :, 3] * reforco_alfa, 0, 1)
    lum = luminancia(px)
    rel = lum / max(float(lum[px[:, :, 3] > 0.5].mean()) if (px[:, :, 3] > 0.5).any() else 1.0, 1e-3)
    tom = (1 - detalhe) + detalhe * np.clip(rel, 0, 2.5)
    for c in range(3):
        px[:, :, c] = cor[c] * tom
    gravar_pixels(img, px)


def apagar_logos(px):
    """Pinta por cima dos logos com a cor da camiseta em volta, linha a linha."""
    h, w = px.shape[:2]
    for x0, y0, x1, y1 in LOGOS_NA_CAMISETA:
        c0, c1 = int(x0 * w), int(x1 * w)
        l0, l1 = int((1 - y1) * h), int((1 - y0) * h)
        bloco = px[l0:l1, c0:c1, :3]
        sat = bloco.max(axis=2) - bloco.min(axis=2)
        lum = bloco.mean(axis=2)
        logo = (sat > 0.08) | (lum < 0.72)
        for _ in range(4):
            m = logo.copy()
            m[1:, :] |= logo[:-1, :]
            m[:-1, :] |= logo[1:, :]
            m[:, 1:] |= logo[:, :-1]
            m[:, :-1] |= logo[:, 1:]
            logo = m
        limpo = ~logo
        media_geral = bloco[limpo].mean(axis=0) if limpo.any() else np.array([0.9, 0.9, 0.9])
        for i in range(bloco.shape[0]):
            linha = bloco[i]
            cor = linha[limpo[i]].mean(axis=0) if limpo[i].sum() > 8 else media_geral
            linha[logo[i]] = cor
        px[l0:l1, c0:c1, :3] = bloco


def processar_roupa(img):
    px = pixels(img)
    apagar_logos(px)
    lum = luminancia(px)
    rel = lum / max(float(lum.mean()), 1e-3)
    for c in range(3):
        px[:, :, c] = ROUPA_PRETO[c] * np.clip(1 + ROUPA_DETALHE * (rel - 1), 0.4, 2.0)
    gravar_pixels(img, px)


def processar_tenis(img, sola):
    px = pixels(img)
    lum = luminancia(px)
    rel = np.clip(lum / max(float(lum.mean()), 1e-3), 0, 2)
    s = desfocar(sola, 1)[:, :, None]
    escuro = np.array(TENIS_PRETO, np.float32) * (0.7 + 0.3 * rel)[:, :, None]
    claro = np.array(TENIS_SOLA, np.float32) * (0.9 + 0.1 * rel)[:, :, None]
    px[:, :, :3] = escuro * (1 - s) + claro * s
    gravar_pixels(img, px)


def trocar_textura_dos_olhos(dados):
    velho, novo = TEXTURA_DOS_OLHOS
    for img in bpy.data.images:
        if img.filepath and os.path.basename(bpy.path.abspath(img.filepath)) == velho:
            img.filepath = os.path.join(dados, novo)
            img.reload()


def preparar_texturas(camadas_pele, tenis):
    os.makedirs(PASTA_FBX, exist_ok=True)
    feitas = {}
    for img in list(bpy.data.images):
        if not img.filepath_raw and not img.filepath:
            continue
        origem = bpy.path.abspath(img.filepath_raw or img.filepath)
        nome = os.path.basename(origem)
        if nome in feitas or img.name == "Render Result":
            continue
        if img.users == 0:
            continue
        w, h = img.size
        if max(w, h) > MAX_TEXTURA:
            f = MAX_TEXTURA / max(w, h)
            img.scale(int(w * f), int(h * f))
        final = NOMES_DAS_TEXTURAS.get(nome, nome)
        if nome in ("barba_diffuse", "cabelo_diffuse") or final in ("barba_diffuse.png",
                                                                     "cabelo_diffuse.png"):
            continue                       # geradas por este script; salvas no main()
        if final == "pele_diffuse.png":
            if img.size[1] != MAX_TEXTURA:
                raise RuntimeError("textura da pele não está em %d px" % MAX_TEXTURA)
            processar_pele(img, camadas_pele)
        elif final == "olhos_diffuse.png":
            processar_olhos(img)
        elif final == "sobrancelhas_diffuse.png":
            tingir_com_alfa(img, SOBRANCELHAS_COR, detalhe=0.35, reforco_alfa=SOBRANCELHAS_ALFA)
        elif final == "roupa_diffuse.png":
            processar_roupa(img)
        elif final == "tenis_diffuse.png":
            processar_tenis(img, mascara_da_sola(tenis, img.size[0]))
        destino = os.path.join(PASTA_FBX, final)
        img.filepath_raw = destino
        img.file_format = "PNG"
        img.save()
        img.filepath = destino
        feitas[final] = tuple(img.size[:])
    return feitas


def apagar_texturas_antigas(feitas):
    """Remove do Unity as texturas que não são mais usadas e os materiais extraídos (o Unity
    os recria apontando para as texturas novas)."""
    for caminho in glob.glob(os.path.join(PASTA_FBX, "*.png")):
        if os.path.basename(caminho) not in feitas:
            for f in (caminho, caminho + ".meta"):
                if os.path.exists(f):
                    os.remove(f)
            log("textura antiga removida:", os.path.basename(caminho))
    for caminho in glob.glob(os.path.join(PASTA_FBX, "Materials", "*.mat")):
        for f in (caminho, caminho + ".meta"):
            if os.path.exists(f):
                os.remove(f)


# ---------------------------------------------------------------- nomes, export

def renomear(base, rig, filhos):
    base.name = base.data.name = "Corpo"
    rig.name = "Personagem"
    rig.data.name = "Personagem_Esqueleto"
    for nome, (obj, _) in filhos.items():
        obj.name = obj.data.name = nome
    for o in [base] + [f[0] for f in filhos.values()]:
        for i, slot in enumerate(o.material_slots):
            if slot.material:
                slot.material.name = "M_" + o.name + ("" if i == 0 else "_%d" % i)


def suavizar(objs):
    """Sombreamento suave em tudo (tira a quina dura na linha queixo/pescoço)."""
    for o in objs:
        if o.name == "Oculos":
            continue
        for p in o.data.polygons:
            p.use_smooth = True


def exportar_fbx(rig):
    bpy.ops.object.select_all(action="DESELECT")
    rig.select_set(True)
    for c in rig.children_recursive:
        c.select_set(True)
    bpy.ops.export_scene.fbx(
        filepath=FBX, use_selection=True, object_types={"ARMATURE", "MESH"},
        use_mesh_modifiers=False, mesh_smooth_type="FACE", add_leaf_bones=False,
        primary_bone_axis="Y", secondary_bone_axis="X", armature_nodetype="NULL",
        bake_anim=False, apply_unit_scale=True, apply_scale_options="FBX_SCALE_ALL",
        axis_forward="-Z", axis_up="Y", path_mode="RELATIVE", use_tspace=True)


# ---------------------------------------------------------------- render

def girar_osso(rig, nome, direcao):
    """Gira o osso (pose) para a sua direção no mundo ficar igual a `direcao`."""
    pb = rig.pose.bones[nome]
    bpy.context.view_layer.update()
    m = rig.matrix_world @ pb.matrix
    atual = (m.to_3x3() @ Vector((0, 1, 0))).normalized()
    q = atual.rotation_difference(direcao.normalized())
    cabeca = m.translation.copy()
    novo = Matrix.Translation(cabeca) @ q.to_matrix().to_4x4() @ Matrix.Translation(-cabeca) @ m
    pb.matrix = rig.matrix_world.inverted() @ novo
    bpy.context.view_layer.update()


def posar_relaxado(rig):
    """Braços caídos ao lado do corpo, cotovelo levemente dobrado (só para o render)."""
    for lado, s in (("Left", 1), ("Right", -1)):
        girar_osso(rig, "mixamorig:%sArm" % lado, Vector((s * 0.20, 0.03, -1)))
        girar_osso(rig, "mixamorig:%sForeArm" % lado, Vector((s * 0.12, -0.16, -1)))
        girar_osso(rig, "mixamorig:%sHand" % lado, Vector((s * 0.10, -0.10, -1)))


def renderizar(rig, base):
    cena = bpy.context.scene
    motores = [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items]
    cena.render.engine = "BLENDER_EEVEE_NEXT" if "BLENDER_EEVEE_NEXT" in motores else "BLENDER_EEVEE"
    cena.render.film_transparent = False
    cena.view_settings.view_transform = "Standard"
    mundo = bpy.data.worlds.new("Fundo") if not cena.world else cena.world
    cena.world = mundo
    mundo.use_nodes = True
    bg = mundo.node_tree.nodes.get("Background")
    bg.inputs[0].default_value = (0.62, 0.63, 0.65, 1)
    bg.inputs[1].default_value = 0.6

    def luz(nome, tipo, energia, rot, pos=(0, 0, 0), tamanho=None, angulo=None):
        d = bpy.data.lights.new(nome, tipo)
        d.energy = energia
        if tamanho:
            d.size = tamanho
        if angulo is not None:
            d.angle = math.radians(angulo)   # sol "largo": sombra macia, sem linha dura no pescoço
        o = bpy.data.objects.new(nome, d)
        o.location = pos
        o.rotation_euler = [math.radians(a) for a in rot]
        cena.collection.objects.link(o)
        return o

    luz("Sol", "SUN", 3.0, (50, 0, -30), angulo=14)
    luz("Preenchimento", "AREA", 300, (70, 0, 40), pos=(-2.2, -2.5, 2.2), tamanho=2.5)

    cam_d = bpy.data.cameras.new("Camera")
    cam = bpy.data.objects.new("Camera", cam_d)
    cena.collection.objects.link(cam)
    cena.camera = cam

    altura = max((rig.matrix_world @ Vector(c)).z for o in [base] for c in o.bound_box)
    cabeca = rig.pose.bones.get("mixamorig:Head")
    pos_cabeca = rig.matrix_world @ cabeca.head if cabeca else Vector((0, 0, altura - 0.12))

    def olhar(alvo, distancia, azimute, lente):
        a = math.radians(azimute)
        cam.location = alvo + Vector((math.sin(a) * distancia, -math.cos(a) * distancia, 0))
        direcao = alvo - cam.location
        cam.rotation_euler = direcao.to_track_quat("-Z", "Y").to_euler()
        cam_d.lens = lente

    def salvar(nome, w, h):
        cena.render.resolution_x, cena.render.resolution_y = w, h
        cena.render.filepath = os.path.join(CAPTURAS, nome)
        bpy.ops.render.render(write_still=True)
        log("render:", cena.render.filepath)

    malhas = [base] + [c for c in rig.children_recursive if c.type == "MESH" and c != base]

    def chaves(valores):
        for o in malhas:
            if o.data.shape_keys:
                for k in o.data.shape_keys.key_blocks:
                    k.value = valores.get(k.name, 0.0)

    os.makedirs(CAPTURAS, exist_ok=True)
    centro = Vector((0, 0, altura / 2))
    olhar(centro, 3.6, 0, 50)
    salvar("personagem-frente.png", 1080, 1440)
    olhar(centro, 3.6, -35, 50)
    salvar("personagem-34.png", 1080, 1440)
    rosto = pos_cabeca + Vector((0, 0, 0.03))
    olhar(rosto, 0.95, -12, 85)
    salvar("personagem-rosto.png", 1080, 1080)
    chaves({"Rosto_Sorriso": 1.0})
    salvar("personagem-rosto-sorriso.png", 1080, 1080)
    chaves({"Rosto_OlhosCansados": 1.0})
    salvar("personagem-rosto-cansado.png", 1080, 1080)
    chaves({})

    # como aparece no jogo (CenaDoQuarto.MontarCamera): câmera a 4,2 m, 1,1 m de altura,
    # olhando para (0, 0,9, 0), FOV vertical 35°, faixa de 1080 x 806; pose relaxada.
    posar_relaxado(rig)
    cam.location = Vector((0, -4.2, 1.1))
    cam.rotation_euler = (Vector((0, 0, 0.9)) - cam.location).to_track_quat("-Z", "Y").to_euler()
    cam_d.sensor_fit = "VERTICAL"
    cam_d.angle_y = math.radians(35)
    bg.inputs[0].default_value = (0.83, 0.89, 0.97, 1)
    salvar("personagem-jogo.png", 1080, 806)
    cam_d.sensor_fit = "AUTO"
    for pb in rig.pose.bones:
        pb.matrix_basis = Matrix.Identity(4)
    bpy.context.view_layer.update()


# ---------------------------------------------------------------- principal

def main():
    zip_assets, render = argumentos()
    dados = garantir_assets(zip_assets)
    limpar_cena()

    base, rig, filhos = criar_humano(dados)
    deltas = capturar_deltas(base)
    aplicar_shape_keys(base, filhos, deltas)

    # ajustes que precisam da pele inteira (antes de apagar o que fica sob a roupa)
    ref = referencias_da_cabeca(base, rig)
    reforcar_trapezio(base, rig)
    bvh = bvh_da_pele(base)
    guardar_pesos_da_barba(base, pesos_da_barba(ref))
    afunilar_calca(filhos["Roupa"][0], base, bvh, rig)

    altura = altura_da_pele(base)
    limpar_corpo(base, rig)
    renomear(base, rig, filhos)
    partes = separar_roupa(filhos["Roupa"][0])
    for nome, obj in partes.items():
        obj.name = obj.data.name = nome
    if "Camiseta" in partes:
        justar_camiseta(partes["Camiseta"], bvh, rig)

    barba, camada_barba = criar_barba(base)
    cabelo, camada_cabelo = criar_cabelo(base, ref)
    oculos = criar_oculos(base, rig, bvh_atual(base), ref)

    malhas = [o for o in rig.children_recursive if o.type == "MESH"]
    suavizar(malhas)
    for mat in bpy.data.materials:
        bsdf = mat.node_tree.nodes.get("Principled BSDF") if mat.node_tree else None
        if bsdf and mat.name in ASPEREZA:
            bsdf.inputs["Roughness"].default_value = ASPEREZA[mat.name]
    trocar_textura_dos_olhos(dados)
    texturas = preparar_texturas([camada_cabelo, camada_barba], filhos["Tenis"][0])
    for obj in (barba, cabelo):
        img = obj.data.materials[0].node_tree.nodes["Image Texture"].image
        img.save()
        texturas[os.path.basename(img.filepath_raw)] = tuple(img.size[:])

    total = 0
    for o in sorted(malhas, key=lambda o: o.name):
        t = triangulos(o)
        total += t
        chaves = [k.name for k in o.data.shape_keys.key_blocks] if o.data.shape_keys else []
        log("malha %-14s tris=%6d  materiais=%s  shape_keys=%s" % (
            o.name, t, [s.material.name for s in o.material_slots if s.material], chaves))
    log("TOTAL de triângulos:", total)
    for nome, tam in sorted(texturas.items()):
        log("textura", nome, tam)
    log("altura da pele: %.3f m (height=%.3f)" % (altura, MACROS["height"]))
    log("ossos:", len(rig.data.bones))

    os.makedirs(os.path.dirname(BLEND), exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=BLEND)
    exportar_fbx(rig)
    apagar_texturas_antigas(texturas)
    log("salvo", BLEND, FBX)

    if render:
        renderizar(rig, base)
        bpy.ops.wm.save_as_mainfile(filepath=BLEND)


main()
