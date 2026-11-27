using System.Text.Json;
using System.Text.Json.Serialization;

namespace IabsAgenteSenior.Configuracao;

/// <summary>
/// O config.json (2.2.0): as conexões do integrador. Cada conexão liga um
/// ambiente da IABS (endereço e chave próprios) a um banco do Senior, ex.:
/// "Produção" com o Senior de produção e "Homologação" com o de homologação,
/// noutro servidor. O serviço mantém um canal com a IABS por conexão.
/// O arquivo da 2.1 (uma conexão só, com os campos no topo) é lido como a
/// conexão "Produção" e passa ao formato novo no próximo Salvar.
/// </summary>
public sealed class ConfiguracaoIntegrador
{
    public List<ConfiguracaoLocal> Conexoes { get; set; } = [];

    /// <summary>Proxy da máquina para o GitHub (atualização): o da primeira conexão.</summary>
    [JsonIgnore] public ConfiguracaoProxy ProxyPrincipal => Conexoes.FirstOrDefault()?.Proxy ?? new ConfiguracaoProxy();

    public ConfiguracaoLocal? Achar(string? nome) =>
        nome is null ? null : Conexoes.FirstOrDefault(c => MesmoNome(c.Nome, nome));

    public static bool MesmoNome(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.CurrentCultureIgnoreCase);

    public static ConfiguracaoIntegrador? Carregar(Caminhos caminhos)
    {
        if (!File.Exists(caminhos.ArquivoConfig)) return null;
        return Ler(File.ReadAllText(caminhos.ArquivoConfig));
    }

    /// <summary>Lê o formato novo (lista "conexoes") ou o da 2.1 (uma conexão no topo).</summary>
    public static ConfiguracaoIntegrador Ler(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var raiz = doc.RootElement;
        if (raiz.ValueKind != JsonValueKind.Object) throw new JsonException("A configuração não é um objeto JSON.");

        ConfiguracaoIntegrador cfg;
        if (raiz.EnumerateObject().Any(p => string.Equals(p.Name, "conexoes", StringComparison.OrdinalIgnoreCase)))
        {
            cfg = raiz.Deserialize<ConfiguracaoIntegrador>(ConfiguracaoLocal.Json) ?? new ConfiguracaoIntegrador();
            cfg.Conexoes ??= []; // "conexoes": null
            cfg.Conexoes.RemoveAll(c => c is null);
        }
        else
        {
            var unica = raiz.Deserialize<ConfiguracaoLocal>(ConfiguracaoLocal.Json) ?? new ConfiguracaoLocal();
            cfg = new ConfiguracaoIntegrador { Conexoes = [unica] };
        }
        cfg.CompletarNomes();
        return cfg;
    }

    /// <summary>Conexão sem nome (arquivo da 2.1 ou editado à mão) ganha um, sem repetir.</summary>
    private void CompletarNomes()
    {
        foreach (var c in Conexoes)
        {
            c.Banco ??= new ConfiguracaoBanco();
            c.Proxy ??= new ConfiguracaoProxy();
        }
        var usados = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var c in Conexoes)
        {
            var nome = string.IsNullOrWhiteSpace(c.Nome) ? ConfiguracaoLocal.NomePadrao : c.Nome.Trim();
            var candidato = nome;
            for (var i = 2; !usados.Add(candidato); i++) candidato = $"{nome} {i}";
            c.Nome = candidato;
        }
    }

    public void Salvar(Caminhos caminhos)
    {
        Directory.CreateDirectory(caminhos.PastaDados);
        var temporario = caminhos.ArquivoConfig + ".novo";
        File.WriteAllText(temporario, JsonSerializer.Serialize(this, ConfiguracaoLocal.Json));
        File.Move(temporario, caminhos.ArquivoConfig, overwrite: true);
    }

    /// <summary>Para o pacote de diagnóstico: sem nenhum segredo.</summary>
    public object SemSegredos() => Conexoes.Select(c => c.SemSegredos()).ToList();
}
