using System;
using UnityEngine;

namespace JogoDaVida
{
    /// <summary>
    /// Conta fixa para uso pessoal (sem tela de login). Lida de
    /// Assets/JogoDaVida/Resources/conta_local.json, que fica fora do Git:
    /// { "email": "...", "senha": "..." }
    /// Se o arquivo não existir, o app mostra a tela de login normal.
    /// </summary>
    [Serializable]
    public class ContaLocal
    {
        public string email;
        public string senha;

        static bool lida;
        static ContaLocal conta;

        public static ContaLocal Carregar()
        {
            if (lida) return conta;
            lida = true;
            var arquivo = Resources.Load<TextAsset>("conta_local");
            if (arquivo == null) return null;
            var c = JsonUtility.FromJson<ContaLocal>(arquivo.text);
            if (c != null && !string.IsNullOrEmpty(c.email) && !string.IsNullOrEmpty(c.senha)) conta = c;
            return conta;
        }
    }
}
