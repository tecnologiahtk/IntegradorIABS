# Integrador IABS

Programa da IABS instalado no servidor Windows do cliente. É a ponte entre a IABS e o banco do Senior (SQL Server) na rede interna do cliente:

- abre **uma conexão de saída** (WebSocket sobre HTTPS) com a IABS e a mantém aberta; nenhuma porta de entrada é aberta no cliente;
- por ela, a IABS pede **operações fixas**: ler os colaboradores, conferir e lançar os atestados na r038afa; o SQL de cada operação está dentro do integrador (`src\IabsAgenteSenior\Banco\OperacoesSenior.cs`), e a IABS nunca manda SQL;
- a **senha do banco fica só aqui**, cifrada pelo Windows; a IABS só conhece a chave do integrador.

Na IABS, a conexão é a opção **Integrador IABS** em Governança → Configurações → Integrações. O contrato entre os dois lados está em [`..\docs\protocolo-canal.md`](../docs/protocolo-canal.md).

## Pastas

| Pasta | Conteúdo |
|---|---|
| `src\IabsAgenteSenior` | o integrador (.NET 10, serviço do Windows + comandos): `Canal\` (conexão com a IABS), `Banco\` (operações no Senior) |
| `src\IabsAgenteSenior.Configurador` | a janela de configuração (Windows Forms), publicada na mesma pasta |
| `src\IabsAgenteSenior.Monitor` | o ícone perto do relógio (sem administrador), publicado na mesma pasta |
| `tests\IabsAgenteSenior.Testes` | testes de unidade e do canal de ponta a ponta (uma "IABS" local em Kestrel) |
| `instalador` | o MSI (WiX 5) |
| `publicar` | saída do build: `app\` (pasta publicada) e `msi\` (instalador) — gerada, não versionar |

O nome interno continua o da versão 1 (`IabsAgenteSenior`: serviço, pastas e executáveis), para atualizar sem desinstalar. O nome que a pessoa vê é **Integrador IABS**.

## Pré-requisitos (só na máquina de quem compila)

- .NET SDK 10 (`winget install Microsoft.DotNet.SDK.10`). O cliente **não** precisa: o runtime vai dentro da pasta do integrador.

## Compilar, testar e gerar o instalador

```cmd
dotnet test tests\IabsAgenteSenior.Testes

rem apague publicar\app antes: o MSI leva tudo que estiver lá
rmdir /s /q publicar\app
dotnet publish src\IabsAgenteSenior -c Release -o publicar\app
dotnet publish src\IabsAgenteSenior.Configurador -c Release -o publicar\app
dotnet publish src\IabsAgenteSenior.Monitor -c Release -o publicar\app
dotnet build instalador\IabsAgenteSenior.Instalador.wixproj -c Release --no-incremental
rem resultado: publicar\msi\IntegradorIABS-2.0.0.msi
```

Para outra versão: suba `<Version>` em `Directory.Build.props` e `Versao` no `.wixproj` (ou `-p:Versao=2.0.1`). O MSI vai como Release no GitHub (não no git).

Avisos conhecidos do WiX, sem efeito: WIX1149 (ServiceConfig: o início automático atrasado funciona, conferido com `sc qc`), ICE69 (atalhos) e ICE60 (idioma de um arquivo do runtime).

## Testar localmente sem instalar

Com a IABS rodando no computador (`http://127.0.0.1:5000`) e uma conexão **Integrador IABS** criada nela, com a chave gerada:

```cmd
dotnet run --project src\IabsAgenteSenior -- configurar --arquivo entrada.json --dados .teste
dotnet run --project src\IabsAgenteSenior -- testar-banco --dados .teste
dotnet run --project src\IabsAgenteSenior -- testar-iabs  --dados .teste
dotnet run --project src\IabsAgenteSenior -- servico      --dados .teste
```

Exemplo de `entrada.json` (o arquivo é apagado depois de lido):

```json
{
  "apiUrl": "http://127.0.0.1:5000",
  "chaveApi": "<chave gerada na IABS>",
  "banco": { "servidor": "srv-senior", "porta": 1433, "banco": "SENIOR", "schema": "dbo", "usuario": "iabs_integrador" },
  "senhaBanco": "<senha>",
  "gravacaoLiberada": false
}
```

## Instalar no servidor do cliente

1. Rodar o MSI como administrador. Ele confere os requisitos (Windows 64 bits, Windows Server 2019 / build 17763 ou mais novo, administrador), mostra a licença, **pergunta a pasta** (padrão `C:\Program Files\IABS\AgenteSenior`; nas atualizações fica a mesma) e instala o serviço "Integrador IABS" (conta virtual `NT SERVICE\IabsAgenteSenior`, início automático atrasado, reinício em caso de falha), o atualizador (início manual) e os atalhos: **Integrador IABS** na Área de Trabalho e "Configurar Integrador IABS" no menu Iniciar.
2. No fim, abre a janela **Configurar Integrador IABS** (também pelos atalhos):
   - aba 1: endereço da IABS e a chave do integrador (gerada na IABS, na conexão Integrador IABS);
   - aba 2: o SQL Server do Senior e, se a empresa quiser que a IABS lance os atestados, a caixa **Permitir que a IABS lance os atestados**;
   - testar e Salvar. A aba 3 mostra se o canal está conectado e o último pedido.
3. Sem interface gráfica (Server Core) ou em instalação silenciosa: `"C:\Program Files\IABS\AgenteSenior\IabsAgenteSenior.exe" configurar` (assistente) ou `configurar --arquivo config.json`.
4. O serviço se conecta à IABS em até 15 segundos depois de salvar e fica conectado.

Comandos: `requisitos`, `configurar`, `testar-banco`, `testar-iabs`, `diagnostico`, `servico`, `atualizador`, `versao`. `IabsAgenteSenior.exe ajuda` mostra todos.

### O que pedir à TI do cliente

- **Rede:** saída HTTPS (443) do servidor para o endereço da IABS, com WebSocket liberado no proxy, se houver. Para a atualização automática (2.1.0), também `github.com`, `api.github.com` e `*.githubusercontent.com` (de onde o atualizador baixa a versão nova).
- **Banco:** um login no SQL Server do Senior com leitura nas tabelas `r034fun`, `r034cpl`, `r016orn`, `r018ccu`, `r024car`, `r030fil`, `r006esc`, `r010sit` e `r038afa`; para lançar os atestados, também INSERT, UPDATE e DELETE na `r038afa`.

## Atualizar

Desde a 2.1.0, a IABS atualiza o integrador: na tela de Integrações aparece "versão nova disponível → Atualizar". O pedido vai pelo canal e o integrador liga o serviço **Integrador IABS (atualizador)** (início manual, como SYSTEM). Ele:

1. só aceita versão mais nova que a instalada;
2. pega na API do GitHub o SHA-256 do `IntegradorIABS.msi` daquela versão, neste repositório, e confere com o que a IABS mandou;
3. baixa o instalador (só HTTPS, só GitHub) e confere o SHA-256 do arquivo;
4. roda `msiexec /i ... /qn` e se desliga. O instalador para o integrador, troca os arquivos e liga de novo; a configuração fica.

Se qualquer conferência falhar, nada é instalado, e o motivo aparece na tela da IABS e na janela (`atualizacao.json` na pasta de dados).

A TI também pode atualizar pela janela: aba **3. Situação** → **Verificar atualização** (consulta a última versão publicada neste repositório) → **Atualizar para X**. Vale o mesmo atualizador e a mesma conferência (SHA-256 do GitHub e do arquivo; o da IABS só entra quando quem pede é a tela da IABS). Durante a instalação a janela fecha; depois, abra pelo atalho. À mão, também vale rodar o `IntegradorIABS.msi` novo por cima.

## Desinstalar

Painel de Controle → Programas → "Integrador IABS" → Desinstalar. Remove os dois serviços (o integrador e o atualizador), `C:\Program Files\IABS\AgenteSenior`, `C:\ProgramData\IABS\AgenteSenior` (configuração, situação e logs) e, desde a 2.1.0, a lista de quem abre a janela sem administrador (`HKLM\SOFTWARE\IABS\AgenteSenior`). Na atualização de versão os dados ficam. Para remover o programa e manter os dados: `msiexec /x IntegradorIABS-2.0.0.msi MANTERDADOS=1`.

Não saem com a desinstalação: o login do banco (DBA), regras de firewall e proxy (TI) e a chave do integrador (gere outra ou exclua a conexão na IABS).

## O que o integrador nunca faz

Roda SQL vindo de fora; baixa ou instala programa que não seja o instalador oficial deste repositório, conferido pelo SHA-256 da IABS, do GitHub e do arquivo; grava em tabela que não seja a r038afa; altera ou apaga linha sem a marca da IABS ("IABS #…" no obsafa); grava sem a caixa de liberação marcada; abre porta de entrada; chama PowerShell, cmd ou outro processo (o atualizador chama só o `msiexec`); roda como administrador (só o atualizador roda como SYSTEM, e só para instalar); grava fora de `C:\ProgramData\IABS\AgenteSenior`; manda à IABS o texto livre do obsafa ou a mensagem de erro do banco; registra CPF, nome, CID ou e-mail em log (o log mascara o que parecer CPF ou e-mail).

## Pendências

- Assinatura de código (opcional): o MSI e os binários saem sem assinatura.
- Teste numa VM Windows Server limpa com Defender for Endpoint.
- Oracle: saiu na versão 2 (as operações são só SQL Server). Volta quando um cliente precisar.
