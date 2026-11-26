using System.Reflection;

namespace IabsAgenteSenior;

public static class Constantes
{
    // Nome do serviço, conta e pastas continuam os da versão 1 (IabsAgenteSenior):
    // trocar exigiria desinstalar e reinstalar no cliente. O nome que a pessoa vê
    // é "Integrador IABS", o mesmo da opção na tela de Integrações da IABS.
    public const string NomeServico = "IabsAgenteSenior";
    public const string NomeExibicao = "Integrador IABS";
    public const string ContaServico = @"NT SERVICE\IabsAgenteSenior";

    /// <summary>Versão do protocolo do canal (docs/protocolo-canal.md).</summary>
    public const int Protocolo = 1;

    public static string Versao { get; } =
        typeof(Constantes).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static string UserAgent => $"IABS-Integrador/{Versao}";
}
