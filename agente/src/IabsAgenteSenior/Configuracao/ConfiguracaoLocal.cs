using System.Text.Json;
using System.Text.Json.Serialization;

namespace IabsAgenteSenior.Configuracao;

/// <summary>
/// O que fica só na máquina do cliente: endereço da IABS, chave do integrador,
/// conexão do banco do Senior e a liberação de gravação. Segredos sempre
/// cifrados (campos *Protegida). A senha do banco nunca vai para a IABS.
/// </summary>
public sealed class ConfiguracaoLocal
{
    /// <summary>Endereço da IABS (ex.: https://app.iabs.med.br). Só o domínio conta: o caminho do canal é fixo.</summary>
    public string ApiUrl { get; set; } = "";
    public string ChaveApiProtegida { get; set; } = "";
    public ConfiguracaoBanco Banco { get; set; } = new();
    public ConfiguracaoProxy Proxy { get; set; } = new();

    /// <summary>
    /// A TI do cliente permite que a IABS lance os atestados no Senior (r038afa).
    /// Desligado, as operações de gravação são recusadas (GRAVACAO_DESLIGADA).
    /// </summary>
    public bool GravacaoLiberada { get; set; }

    [JsonIgnore] public string ChaveApi => Protetor.Abrir(ChaveApiProtegida);

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static ConfiguracaoLocal? Carregar(Caminhos caminhos)
    {
        if (!File.Exists(caminhos.ArquivoConfig)) return null;
        return JsonSerializer.Deserialize<ConfiguracaoLocal>(File.ReadAllText(caminhos.ArquivoConfig), Json);
    }

    public void Salvar(Caminhos caminhos)
    {
        Directory.CreateDirectory(caminhos.PastaDados);
        var temporario = caminhos.ArquivoConfig + ".novo";
        File.WriteAllText(temporario, JsonSerializer.Serialize(this, Json));
        File.Move(temporario, caminhos.ArquivoConfig, overwrite: true);
    }

    /// <summary>Versão para o pacote de diagnóstico: sem nenhum segredo.</summary>
    public object SemSegredos() => new
    {
        ApiUrl,
        ChaveApi = string.IsNullOrEmpty(ChaveApiProtegida) ? "não definida" : "definida",
        GravacaoLiberada,
        Banco = new
        {
            Banco.Servidor, Banco.Porta, Banco.Instancia, Banco.Banco, Banco.Schema,
            Banco.AutenticacaoWindows, Banco.Usuario,
            Senha = string.IsNullOrEmpty(Banco.SenhaProtegida) ? "não definida" : "definida",
            Banco.ConfiarCertificadoServidor, Banco.TempoLimiteConsultaSegundos,
        },
        Proxy = new { Proxy.Modo, Proxy.Endereco, Proxy.Usuario },
    };
}

/// <summary>Banco do Senior (SQL Server).</summary>
public sealed class ConfiguracaoBanco
{
    public string Servidor { get; set; } = "";
    public int? Porta { get; set; }
    public string? Instancia { get; set; }
    public string? Banco { get; set; }
    public string? Schema { get; set; } = "dbo";
    public bool AutenticacaoWindows { get; set; }
    public bool ConfiarCertificadoServidor { get; set; }
    public string? Usuario { get; set; }
    public string? SenhaProtegida { get; set; }
    public int TempoLimiteConsultaSegundos { get; set; } = 120;

    [JsonIgnore] public string? Senha => string.IsNullOrEmpty(SenhaProtegida) ? null : Protetor.Abrir(SenhaProtegida);
}

public sealed class ConfiguracaoProxy
{
    /// <summary>sistema (padrão), nenhum ou manual.</summary>
    public string Modo { get; set; } = "sistema";
    public string? Endereco { get; set; }
    public string? Usuario { get; set; }
    public string? SenhaProtegida { get; set; }
}

/// <summary>
/// Arquivo de entrada do "configurar --arquivo": valores em texto, cifrados ao
/// gravar. O arquivo é apagado depois de lido.
/// </summary>
public sealed class EntradaConfiguracao
{
    public string ApiUrl { get; set; } = "";
    public string ChaveApi { get; set; } = "";
    public ConfiguracaoBanco Banco { get; set; } = new();
    public string? SenhaBanco { get; set; }
    public ConfiguracaoProxy? Proxy { get; set; }
    public string? SenhaProxy { get; set; }
    public bool GravacaoLiberada { get; set; }
}
