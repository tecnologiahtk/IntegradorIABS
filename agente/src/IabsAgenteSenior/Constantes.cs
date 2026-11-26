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

    // ---- atualização (2.1.0) ---------------------------------------------------------
    /// <summary>Serviço do atualizador: roda como SYSTEM, só quando o integrador pede, e instala a versão nova.</summary>
    public const string NomeServicoAtualizador = "IabsIntegradorAtualizador";

    /// <summary>O único lugar de onde o atualizador baixa: as versões publicadas do repositório oficial.</summary>
    public const string RepositorioOficial = "tecnologiahtk/IntegradorIABS";

    /// <summary>Nome fixo do instalador em cada versão publicada.</summary>
    public const string ArquivoInstalador = "IntegradorIABS.msi";
}
