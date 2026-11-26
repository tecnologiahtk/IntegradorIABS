using System.Text;
using System.Text.Json;
using IabsAgenteSenior.Atualizacao;
using IabsAgenteSenior.Banco;
using IabsAgenteSenior.Configuracao;
using IabsAgenteSenior.Diagnostico;
using IabsAgenteSenior.Servico;
using IabsAgenteSenior.Sistema;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

namespace IabsAgenteSenior.Comandos;

/// <summary>
/// Linha de comando do integrador. Sem argumentos e iniciado pelo Windows, roda
/// como serviço. Parsing próprio e simples, sem biblioteca externa.
/// </summary>
public static class Cli
{
    private const string Ajuda = """
        Integrador IABS — ponte entre a IABS e o banco do Senior (só conexão de saída, operações fixas).

        Uso: IabsAgenteSenior.exe <comando> [opções]

        Comandos:
          configurar        Assistente de configuração (como administrador). Testa o banco e a IABS antes de gravar.
                              --arquivo <config.json>  configuração em arquivo (instalação silenciosa); o arquivo é apagado
                              --forcar                 grava mesmo se o teste do banco ou da IABS falhar
          requisitos        Confere os requisitos da máquina.
          testar-banco      Testa a conexão com o banco, a leitura de cada tabela e a permissão na r038afa.
          testar-iabs       Testa o canal com a IABS (valida a chave e fecha, sem derrubar o serviço).
          diagnostico       Gera um pacote .zip para o suporte da IABS, sem dado de colaborador.
          servico           Roda o serviço neste console (para teste).
          atualizador       Roda o atualizador uma vez (como administrador): instala o pedido deixado pela IABS,
                            conferido pelo SHA-256 da IABS, do GitHub e do arquivo.
          versao            Mostra a versão.

        Opção geral:
          --dados <pasta>   Pasta de dados (padrão: C:\ProgramData\IABS\AgenteSenior).
        """;

    public static async Task<int> ExecutarAsync(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var opcoes = Opcoes.Ler(args);
        var caminhos = Caminhos.Resolver(opcoes.Valor("--dados"));

        if (opcoes.Comando is null)
        {
            if (WindowsServiceHelpers.IsWindowsService()) return await RodarServicoAsync(caminhos, console: false);
            Console.WriteLine(Ajuda);
            return 0;
        }

        try
        {
            return opcoes.Comando switch
            {
                "ajuda" or "--help" or "-h" or "/?" => Escrever(Ajuda),
                "versao" or "--version" => Escrever($"{Constantes.NomeExibicao} {Constantes.Versao} (protocolo {Constantes.Protocolo})"),
                "requisitos" => MostrarRequisitos(caminhos),
                "configurar" => await ConfigurarAsync(caminhos, opcoes),
                "testar-banco" => await TestarBancoAsync(CarregarConfig(caminhos), caminhos),
                "testar-iabs" => await TestarIabsAsync(CarregarConfig(caminhos), caminhos),
                "diagnostico" => Escrever($"Pacote gerado: {PacoteDiagnostico.Gerar(caminhos)}"),
                "servico" => await RodarServicoAsync(caminhos, console: true),
                "atualizador" => await RodarAtualizadorAsync(caminhos),
                "remover-dados" => RemoverDados(caminhos, opcoes),
                "preparar-instalacao" => PrepararInstalacao(caminhos, opcoes),
                _ => Escrever($"Comando desconhecido: {opcoes.Comando}\n\n{Ajuda}", 2),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ERRO] {ArquivoLoggerProvider.Mascarar(ex.Message)}");
            Logger(caminhos).LogError("Comando {Comando} falhou ({Tipo}).", opcoes.Comando, ex.GetType().Name);
            return 1;
        }
    }

    private static int Escrever(string texto, int codigo = 0)
    {
        Console.WriteLine(texto);
        return codigo;
    }

    private static ILogger Logger(Caminhos caminhos) =>
        LoggerFactory.Create(b => b.AddProvider(new ArquivoLoggerProvider(caminhos.PastaLogs))).CreateLogger("Cli");

    private static ConfiguracaoLocal CarregarConfig(Caminhos caminhos) =>
        ConfiguracaoLocal.Carregar(caminhos)
        ?? throw new InvalidOperationException($"Integrador sem configuração em {caminhos.ArquivoConfig}. Rode: IabsAgenteSenior.exe configurar");

    // ---- serviço ----------------------------------------------------------------------
    private static async Task<int> RodarServicoAsync(Caminhos caminhos, bool console)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Services.AddWindowsService(o => o.ServiceName = Constantes.NomeServico);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ArquivoLoggerProvider(caminhos.PastaLogs));
        if (console) builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
        builder.Services.AddSingleton(caminhos);
        builder.Services.AddHostedService<ServicoAgente>();
        await builder.Build().RunAsync();
        return 0;
    }

    // ---- atualizador (2.1.0) ---------------------------------------------------------------
    /// <summary>Como serviço (iniciado pelo integrador): roda uma vez e para. No console: roda uma vez e mostra o resultado.</summary>
    private static async Task<int> RodarAtualizadorAsync(Caminhos caminhos)
    {
        if (WindowsServiceHelpers.IsWindowsService())
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], ContentRootPath = AppContext.BaseDirectory });
            builder.Services.AddWindowsService(o => o.ServiceName = Constantes.NomeServicoAtualizador);
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new ArquivoLoggerProvider(caminhos.PastaLogs));
            builder.Services.AddSingleton(caminhos);
            builder.Services.AddHostedService<ServicoAtualizador>();
            await builder.Build().RunAsync();
            return 0;
        }
        var r = await new Atualizador(caminhos, Logger(caminhos)).ExecutarAsync(CancellationToken.None);
        Console.WriteLine($"Atualização para {r.Versao}: {r.Situacao}{(r.Codigo is null ? "" : $" ({r.Codigo})")}.");
        return r.Situacao is "INSTALANDO" ? 0 : 1;
    }

    // ---- requisitos -----------------------------------------------------------------------
    private static int MostrarRequisitos(Caminhos caminhos)
    {
        var itens = Requisitos.Verificar(caminhos.PastaDados);
        foreach (var i in itens) Console.WriteLine($"  [{(i.Ok ? "ok" : "FALTA")}] {i.Nome}: {i.Detalhe}");
        Console.WriteLine("  [ok] Runtime .NET: incluído na pasta do integrador (nada a instalar).");
        return itens.Take(3).All(i => i.Ok) ? 0 : 1;
    }

    // ---- configurar ---------------------------------------------------------------------------
    private static async Task<int> ConfigurarAsync(Caminhos caminhos, Opcoes opcoes)
    {
        var arquivo = opcoes.Valor("--arquivo");
        var entrada = arquivo is not null ? LerEntrada(arquivo) : PerguntarEntrada(ConfiguracaoLocal.Carregar(caminhos));

        var cfg = ServicoConfiguracao.Montar(ConfiguracaoLocal.Carregar(caminhos), entrada);

        Console.WriteLine("Testando o banco do Senior...");
        var bancoOk = await TestarBancoAsync(cfg, caminhos) == 0;
        Console.WriteLine("Testando a conexão com a IABS...");
        var iabsOk = await TestarIabsAsync(cfg, caminhos) == 0;
        if ((!bancoOk || !iabsOk) && !opcoes.Tem("--forcar"))
        {
            Console.WriteLine("Configuração NÃO gravada: corrija os itens acima (ou use --forcar).");
            return 1;
        }

        var aviso = ServicoConfiguracao.Gravar(cfg, caminhos);
        Console.WriteLine($"Configuração gravada em {caminhos.ArquivoConfig}. O serviço se conecta à IABS em até 15 segundos.");
        if (aviso is not null) Console.WriteLine(aviso);
        return 0;
    }

    private static EntradaConfiguracao LerEntrada(string arquivo)
    {
        var entrada = JsonSerializer.Deserialize<EntradaConfiguracao>(File.ReadAllText(arquivo), ConfiguracaoLocal.Json)
            ?? throw new InvalidOperationException("Arquivo de configuração vazio.");
        File.Delete(arquivo); // tem senha em texto: não fica no disco
        Console.WriteLine($"Arquivo {arquivo} lido e apagado.");
        return entrada;
    }

    private static EntradaConfiguracao PerguntarEntrada(ConfiguracaoLocal? atual)
    {
        Console.WriteLine("Configuração do Integrador IABS (Enter mantém o valor entre colchetes).");
        foreach (var r in Requisitos.Verificar(Caminhos.PastaPadrao).Where(r => !r.Ok)) Console.WriteLine($"[AVISO] Requisito: {r.Nome} ({r.Detalhe})");
        var e = new EntradaConfiguracao
        {
            ApiUrl = Perguntar("Endereço da IABS", atual?.ApiUrl),
            ChaveApi = PerguntarSegredo("Chave do integrador (Enter mantém a atual)"),
            Proxy = atual?.Proxy,
        };
        var b = new ConfiguracaoBanco
        {
            Servidor = Perguntar("Servidor do banco do Senior (SQL Server)", atual?.Banco.Servidor),
        };
        // padrão do SQL Server: 1433 (vazio = usar a instância nomeada)
        var porta = Perguntar("Porta (vazio = usar a instância)", atual?.Banco.Porta?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? (atual is null ? "1433" : null), obrigatorio: false);
        b.Porta = int.TryParse(porta, out var p) ? p : null;
        if (b.Porta is null) b.Instancia = Perguntar("Instância (vazio = padrão)", atual?.Banco.Instancia, obrigatorio: false);
        b.Banco = Perguntar("Nome do banco", atual?.Banco.Banco);
        b.Schema = Perguntar("Schema das tabelas", atual?.Banco.Schema ?? "dbo");
        b.AutenticacaoWindows = SimNao("Autenticação do Windows / gMSA?", atual?.Banco.AutenticacaoWindows == true);
        b.ConfiarCertificadoServidor = atual?.Banco.ConfiarCertificadoServidor ?? false;
        if (!b.AutenticacaoWindows)
        {
            b.Usuario = Perguntar("Usuário do banco", atual?.Banco.Usuario);
            e.SenhaBanco = PerguntarSegredo("Senha do banco (Enter mantém a atual)");
        }
        e.Banco = b;
        e.GravacaoLiberada = SimNao("Permitir que a IABS lance os atestados no Senior (r038afa, só linhas com a marca IABS)?", atual?.GravacaoLiberada == true);
        return e;
    }

    private static bool SimNao(string texto, bool padrao) =>
        Perguntar($"{texto} (s/n)", padrao ? "s" : "n").StartsWith("s", StringComparison.OrdinalIgnoreCase);

    private static string Perguntar(string texto, string? padrao, bool obrigatorio = true)
    {
        while (true)
        {
            Console.Write(string.IsNullOrEmpty(padrao) ? $"{texto}: " : $"{texto} [{padrao}]: ");
            var valor = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(valor)) valor = padrao;
            if (!string.IsNullOrEmpty(valor) || !obrigatorio) return valor ?? "";
        }
    }

    private static string PerguntarSegredo(string texto)
    {
        Console.Write($"{texto}: ");
        var sb = new StringBuilder();
        while (true)
        {
            var tecla = Console.ReadKey(intercept: true);
            if (tecla.Key == ConsoleKey.Enter) break;
            if (tecla.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0) sb.Length--;
                continue;
            }
            sb.Append(tecla.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }

    // ---- testes ---------------------------------------------------------------------------------
    private static async Task<int> TestarBancoAsync(ConfiguracaoLocal cfg, Caminhos caminhos)
    {
        try
        {
            var r = await ServicoConfiguracao.TestarBancoAsync(cfg.Banco, Logger(caminhos), CancellationToken.None);
            Console.WriteLine($"  Banco: {r.Banco.Nome} — {r.Banco.Produto} {r.Banco.Versao}");
            if (r.CertificadoNaoValidado)
            {
                Console.WriteLine("  [info] O certificado do SQL Server não é de uma autoridade reconhecida: a conexão é cifrada, sem validar o certificado.");
            }
            foreach (var t in r.Tabelas) Console.WriteLine(t.Ok ? $"  [ok] {t.Tabela}" : $"  [FALTA] {t.Tabela}: {t.Codigo}");
            Console.WriteLine($"  Permissão na r038afa: incluir {(r.Incluir ? "sim" : "não")}, alterar {(r.Alterar ? "sim" : "não")}, excluir {(r.Excluir ? "sim" : "não")}.");
            if (cfg.GravacaoLiberada && !r.GravacaoOk)
            {
                Console.WriteLine("  [ATENÇÃO] O lançamento dos atestados está liberado, mas o usuário do banco não pode incluir, alterar e excluir na r038afa.");
            }
            return r.LeituraOk ? 0 : 1;
        }
        catch (Exception ex) when (ex is ErroOperacao or InvalidOperationException)
        {
            Console.WriteLine($"  [FALHA] {(ex is ErroOperacao e ? TextoDoErro(e.Codigo) : ex.Message)}");
            return 1;
        }
    }

    /// <summary>Texto para a pessoa a partir do código do banco.</summary>
    public static string TextoDoErro(string codigo) => codigo switch
    {
        "BANCO_LOGIN" => "O banco recusou o usuário ou a senha.",
        "BANCO_SEM_ACESSO" => "O usuário entrou no servidor, mas não tem acesso ao banco informado.",
        "BANCO_SEM_RESPOSTA" => "O servidor do banco não respondeu: confira o nome, a porta e o firewall.",
        "LEITURA_RECUSADA" => "O usuário do banco não pode ler uma das tabelas.",
        _ => $"Erro do banco ({codigo}).",
    };

    private static async Task<int> TestarIabsAsync(ConfiguracaoLocal cfg, Caminhos caminhos)
    {
        var fim = await ServicoConfiguracao.TestarIabsAsync(cfg, Logger(caminhos), CancellationToken.None);
        Console.WriteLine(fim.Codigo == "OK"
            ? $"  [ok] Conectado à IABS (conexão \"{fim.ConexaoIabs}\")."
            : $"  [FALHA] {fim.Mensagem}");
        return fim.Codigo == "OK" ? 0 : 1;
    }

    // ---- instalação (chamado pelo MSI, como SYSTEM) ------------------------------------------------------
    /// <summary>Libera para quem instalou a pasta de dados e o serviço: a janela abre sem administrador.</summary>
    private static int PrepararInstalacao(Caminhos caminhos, Opcoes opcoes)
    {
        foreach (var m in PermissoesInstalacao.Preparar(caminhos.PastaDados, opcoes.Valor("--usuario"))) Console.WriteLine(m);
        return 0;
    }

    // ---- desinstalação (chamado pelo MSI) -------------------------------------------------------------
    private static int RemoverDados(Caminhos caminhos, Opcoes opcoes)
    {
        if (opcoes.Tem("--manter")) return 0;
        // a lista de quem abre a janela sem administrador (gravada pela instalação) sai junto (2.1.0)
        Console.WriteLine(PermissoesInstalacao.Limpar());
        var pendentes = PastaDados.Remover(caminhos.PastaDados);
        Console.WriteLine(pendentes == 0
            ? $"Pasta de dados removida: {caminhos.PastaDados}"
            : $"{pendentes} arquivo(s) presos serão apagados na próxima reinicialização.");
        return 0;
    }

    private sealed class Opcoes
    {
        private readonly Dictionary<string, string?> _valores = new(StringComparer.OrdinalIgnoreCase);
        public string? Comando { get; private set; }

        public static Opcoes Ler(string[] args)
        {
            var o = new Opcoes();
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a.StartsWith("--", StringComparison.Ordinal) && a is not "--help" and not "--version")
                {
                    var temValor = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
                    o._valores[a] = temValor ? args[++i] : null;
                }
                else
                {
                    o.Comando ??= a.ToLowerInvariant();
                }
            }
            return o;
        }

        public string? Valor(string nome) => _valores.GetValueOrDefault(nome);
        public bool Tem(string nome) => _valores.ContainsKey(nome);
    }
}
