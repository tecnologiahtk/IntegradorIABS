using System.Security.Cryptography;
using System.Text;

namespace IabsAgenteSenior.Configuracao;

/// <summary>
/// Cifra com DPAPI, escopo de máquina. Quem protege o arquivo de outros usuários
/// da máquina é a permissão da pasta de dados (só SYSTEM, administradores e a
/// conta do serviço).
/// </summary>
public static class Protetor
{
    private static readonly byte[] Entropia = "IABS-AgenteSenior-v1"u8.ToArray();

    public static string Proteger(string texto) =>
        Convert.ToBase64String(ProtegerBytes(Encoding.UTF8.GetBytes(texto)));

    public static string Abrir(string protegido) =>
        Encoding.UTF8.GetString(AbrirBytes(Convert.FromBase64String(protegido)));

    public static byte[] ProtegerBytes(byte[] dados) =>
        ProtectedData.Protect(dados, Entropia, DataProtectionScope.LocalMachine);

    public static byte[] AbrirBytes(byte[] protegido) =>
        ProtectedData.Unprotect(protegido, Entropia, DataProtectionScope.LocalMachine);
}
