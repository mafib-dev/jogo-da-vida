// Não desenha nada (nem cor nem profundidade). O PersonagemHumano usa nas partes da pele que
// ficam debaixo de uma roupa ligada, para o corpo não vazar pela roupa.
Shader "JogoDaVida/Invisivel"
{
    SubShader
    {
        Tags { "Queue" = "Geometry" "RenderType" = "Opaque" }
        Pass
        {
            ColorMask 0
            ZWrite Off
        }
    }
}
