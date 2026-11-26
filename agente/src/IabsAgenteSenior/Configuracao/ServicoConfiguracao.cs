using IabsAgenteSenior.Banco;
using IabsAgenteSenior.Canal;
using IabsAgenteSenior.Sistema;
using Microsoft.Extensions.Logging;

namespace IabsAgenteSenior.Configuracao;

/// <summary>
/// Regras da configuração, usadas pelo assistente de linha de comando e pela
/// janela: cifra dos segredos, endereço HTTPS, teste do banco e do canal antes
/// de gravar, proteção da pasta de dados.
/// </summary>
public static class ServicoConfiguracao
{
    /// <summary>
    /// Monta a configuração a partir do que a pessoa informou. Segredo em branco
    /// mantém o atual (a tela nunca mostra senha nem chave gravadas).
    /// </summary>
    public static ConfiguracaoLocal Montar(ConfiguracaoLocal? atual, EntradaConfiguracao entrada)
    {
        var cfg = new ConfiguracaoLocal
        {
            ApiUrl = entrada.ApiUrl.Trim(),
            ChaveApiProtegida = !string.IsNullOrWhiteSpace(entrada.ChaveApi)
                ? Protetor.Proteger(entrada.ChaveApi.Trim())
                : atual?.ChaveApiProtegida ?? "",
            Banco = entrada.Banco,
            Proxy = entrada.Proxy ?? atual?.Proxy ?? new ConfiguracaoProxy(),
            GravacaoLiberada = entrada.GravacaoLiberada,
        };

        cfg.Banco.SenhaProtegida = cfg.Banco.AutenticacaoWindows ? null
            : !string.IsNullOrEmpty(entrada.SenhaBanco) ? Protetor.Proteger(entrada.SenhaBanco)
            : atual?.Banco.SenhaProtegida;

        if (entrada.Proxy is not null)
        {
            cfg.Proxy.SenhaProtegida = !string.IsNullOrEmpty(entrada.SenhaProxy) ? Protetor.Proteger(entrada.SenhaProxy)
                : atual?.Proxy.SenhaProtegida;
        }

        Validar(cfg);
        return cfg;
    }

    /// <summary>O processo roda elevado (com o controle de conta de usuário, o administrador sem elevar não conta).</summary>
    public static bool EmAdministrador()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    public static void Validar(ConfiguracaoLocal cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.ApiUrl)) throw new InvalidOperationException("Informe o endereço da IABS.");
        Rede.UriDoCanal(cfg.ApiUrl); // https (ou http só no próprio computador)
        if (string.IsNullOrEmpty(cfg.ChaveApiProtegida)) throw new InvalidOperationException("Informe a chave do integrador, gerada na tela de Integrações da IABS.");
        if (string.IsNullOrWhiteSpace(cfg.Banco.Servidor)) throw new InvalidOperationException("Informe o servidor do banco do Senior.");
        if (string.IsNullOrWhiteSpace(cfg.Banco.Banco)) throw new InvalidOperationException("Informe o nome do banco do Senior.");
        if (!cfg.Banco.AutenticacaoWindows && (string.IsNullOrWhiteSpace(cfg.Banco.Usuario) || string.IsNullOrEmpty(cfg.Banco.SenhaProtegida)))
        {
            throw new InvalidOperationException("Informe o usuário e a senha do banco (ou use a autenticação do Windows).");
        }
    }

    public static async Task<ResultadoTeste> TestarBancoAsync(ConfiguracaoBanco banco, ILogger logger, CancellationToken ct)
    {
        var b = new BancoSenior(banco, logger);
        return await new OperacoesSenior(b).TestarAsync(ct);
    }

    /// <summary>Abre o canal em modo teste: a IABS valida a chave, responde e fecha. Não derruba a sessão do serviço.</summary>
    public static Task<FimDaSessao> TestarIabsAsync(ConfiguracaoLocal cfg, ILogger logger, CancellationToken ct)
    {
        var operacoes = new OperacoesSenior(new BancoSenior(cfg.Banco, logger));
        var canal = new CanalIabs(cfg, new Despachante(operacoes, () => false), operacoes.InfoAsync, new EstadoCanal(), logger);
        return canal.TestarAsync(ct);
    }

    /// <summary>
    /// Grava e, na pasta padrão e rodando como administrador, protege a pasta
    /// de dados. Sem administrador (quem instalou abre a janela sem o controle
    /// de conta de usuário), a pasta já foi protegida pelo instalador.
    /// Devolve o aviso da proteção (ou nulo).
    /// </summary>
    public static string? Gravar(ConfiguracaoLocal cfg, Caminhos caminhos)
    {
        cfg.Salvar(caminhos);
        if (!caminhos.EhPastaPadrao || !EmAdministrador()) return null;
        try
        {
            return PastaDados.Proteger(caminhos.PastaDados, PermissoesInstalacao.Configuradores());
        }
        catch (UnauthorizedAccessException)
        {
            return "Sem permissão para proteger a pasta de dados: rode como administrador.";
        }
    }
}
