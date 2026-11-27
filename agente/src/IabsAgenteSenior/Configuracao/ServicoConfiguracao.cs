using System.Text.RegularExpressions;
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
public static partial class ServicoConfiguracao
{
    /// <summary>
    /// Monta uma conexão a partir do que a pessoa informou. Segredo em branco
    /// mantém o atual (a tela nunca mostra senha nem chave gravadas).
    /// </summary>
    public static ConfiguracaoLocal Montar(ConfiguracaoLocal? atual, EntradaConfiguracao entrada)
    {
        var cfg = new ConfiguracaoLocal
        {
            Nome = NomeValido(!string.IsNullOrWhiteSpace(entrada.Nome) ? entrada.Nome
                : !string.IsNullOrWhiteSpace(atual?.Nome) ? atual.Nome
                : ConfiguracaoLocal.NomePadrao),
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

    /// <summary>
    /// Ponto de partida de uma conexão nova (2.2.0): só o proxy da máquina, o da
    /// primeira conexão (com a senha cifrada), que não está na janela.
    /// </summary>
    public static ConfiguracaoLocal BaseDaNova(ConfiguracaoIntegrador? arquivo)
    {
        var p = arquivo?.ProxyPrincipal ?? new ConfiguracaoProxy();
        return new ConfiguracaoLocal
        {
            Proxy = new ConfiguracaoProxy { Modo = p.Modo, Endereco = p.Endereco, Usuario = p.Usuario, SenhaProtegida = p.SenhaProtegida },
        };
    }

    // letras (com acento), números, espaço, hífen, sublinhado e parênteses: sem
    // ponto nem colchete, que o log usa para separar a conexão da categoria
    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N} _()-]{0,39}$")]
    private static partial Regex PadraoNome();

    /// <summary>Nome da conexão (2.2.0): até 40 caracteres, começando por letra ou número.</summary>
    public static string NomeValido(string nome)
    {
        var n = Regex.Replace(nome.Trim(), @"\s+", " ");
        if (!PadraoNome().IsMatch(n))
        {
            throw new InvalidOperationException("Nome da conexão: até 40 letras, números, espaços, hífen, sublinhado ou parênteses (ex.: Produção, Homologação).");
        }
        return n;
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
    /// Confere a conexão contra as outras do arquivo (2.2.0): o nome não se
    /// repete e a chave não é a de outra conexão (dois canais com a mesma chave
    /// se derrubariam na IABS: cada ambiente gera a chave da sua conexão).
    /// </summary>
    /// <param name="substitui">Nome da conexão que esta substitui (troca de nome); nulo = conexão nova.</param>
    public static void ConferirConflitos(ConfiguracaoIntegrador? arquivo, ConfiguracaoLocal cfg, string? substitui)
    {
        if (arquivo is null) return;
        foreach (var outra in arquivo.Conexoes)
        {
            if (substitui is not null && ConfiguracaoIntegrador.MesmoNome(outra.Nome, substitui)) continue;
            if (ConfiguracaoIntegrador.MesmoNome(outra.Nome, cfg.Nome))
            {
                throw new InvalidOperationException($"Já existe uma conexão \"{outra.Nome}\". Escolha outro nome.");
            }
            if (MesmaChave(outra, cfg))
            {
                throw new InvalidOperationException($"A conexão \"{outra.Nome}\" já usa esta chave. Cada conexão usa a chave gerada no seu próprio ambiente da IABS.");
            }
        }
    }

    private static bool MesmaChave(ConfiguracaoLocal a, ConfiguracaoLocal b)
    {
        if (string.IsNullOrEmpty(a.ChaveApiProtegida) || string.IsNullOrEmpty(b.ChaveApiProtegida)) return false;
        try
        {
            return string.Equals(a.ChaveApi, b.ChaveApi, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            return false; // chave que este Windows não abre: quem confere é a IABS
        }
    }

    /// <summary>
    /// Grava a conexão no arquivo, no lugar de <paramref name="substitui"/> (ou
    /// como conexão nova), sem mexer nas outras. Na pasta padrão e rodando como
    /// administrador, protege a pasta de dados. Sem administrador (quem instalou
    /// abre a janela sem o controle de conta de usuário), a pasta já foi
    /// protegida pelo instalador. Devolve o aviso da proteção (ou nulo).
    /// </summary>
    public static string? Gravar(ConfiguracaoLocal cfg, Caminhos caminhos, string? substitui)
    {
        var arquivo = ConfiguracaoIntegrador.Carregar(caminhos) ?? new ConfiguracaoIntegrador();
        ConferirConflitos(arquivo, cfg, substitui);
        var i = substitui is null ? -1 : arquivo.Conexoes.FindIndex(c => ConfiguracaoIntegrador.MesmoNome(c.Nome, substitui));
        if (i >= 0) arquivo.Conexoes[i] = cfg;
        else arquivo.Conexoes.Add(cfg);
        return GravarArquivo(arquivo, caminhos);
    }

    /// <summary>Tira a conexão do arquivo: o serviço fecha o canal dela em até 15 segundos.</summary>
    public static string? Remover(string nome, Caminhos caminhos)
    {
        var arquivo = ConfiguracaoIntegrador.Carregar(caminhos)
            ?? throw new InvalidOperationException("Integrador sem configuração.");
        if (arquivo.Conexoes.RemoveAll(c => ConfiguracaoIntegrador.MesmoNome(c.Nome, nome)) == 0)
        {
            throw new InvalidOperationException($"Não há conexão \"{nome}\".");
        }
        return GravarArquivo(arquivo, caminhos);
    }

    private static string? GravarArquivo(ConfiguracaoIntegrador arquivo, Caminhos caminhos)
    {
        arquivo.Salvar(caminhos);
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
