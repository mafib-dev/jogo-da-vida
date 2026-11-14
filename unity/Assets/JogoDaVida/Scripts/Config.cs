namespace JogoDaVida
{
    /// <summary>
    /// Endereço do Supabase e chave pública (publishable). Essa chave pode ir no app:
    /// sozinha ela não dá acesso a nada, e o banco só libera os dados de quem fez login.
    /// Nunca coloque aqui a service_role nem a chave da LLM.
    /// </summary>
    public static class Config
    {
        public const string SupabaseUrl = "https://mcngqqzushgnhqnabwtv.supabase.co";
        public const string SupabaseChavePublica = "sb_publishable_1X9TllrX0NabJTo7-RUduA_tBjaqmUK";
    }
}
