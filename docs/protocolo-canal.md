# Canal do Integrador IABS — protocolo 1

Contrato entre o **Integrador IABS** (agente instalado no servidor do cliente) e o servidor da IABS (Express, provedor `INTEGRADOR_IABS`). Quem muda um lado muda o outro e este arquivo.

## Ideia

O integrador abre **uma** conexão WebSocket **de saída** com a IABS e a mantém aberta. Pela mesma conexão a IABS pede e o integrador responde. Nenhuma porta de entrada é aberta no cliente.

O integrador só sabe fazer as **operações fixas** abaixo. O SQL de cada uma está dentro dele (`Banco/OperacoesSenior.cs`). A IABS manda só o nome da operação e os parâmetros, nunca SQL. A senha do banco fica só no integrador.

## Conexão

```
wss://<endereço da IABS>/api/integrador/canal
Authorization: Bearer <chave do integrador>
X-Integrador-Versao: 2.0.0
X-Integrador-Protocolo: 1
X-Integrador-Teste: 1          (só no "Testar conexão" da janela: valida e fecha)
```

- `http://` só para `127.0.0.1` e `localhost` (desenvolvimento).
- Recusa no handshake: **401** chave inválida ou trocada; **403** conexão excluída; **426** protocolo antigo (atualize o integrador).
- Uma sessão por conexão da IABS: uma nova (sem `X-Integrador-Teste`) derruba a anterior com o código **4002**.
- Fechamentos da IABS: **4001** chave trocada; **4002** substituída por outra sessão; **4003** conexão excluída.
- Sinal de vida: ping do WebSocket a cada 20 s, dos dois lados. Sem pong em 45 s, a conexão cai e o integrador reconecta (espera 1, 2, 5, 10, 20, 30, 60 s; volta ao início depois de 60 s conectado). Depois de 401/403, espera 5 min; depois de 426, 30 min.
- Compressão `permessage-deflate` ligada (os blocos de colaboradores são grandes).

## Mensagens (texto, JSON)

Integrador → IABS, logo depois de conectar:

```json
{ "tipo": "ola", "versao": "2.0.0", "protocolo": 1, "maquina": "SRV-APP01",
  "banco": { "produto": "SQL Server", "versao": "15.0.2080.9", "nome": "SENIOR" },
  "banco_erro": null, "gravacao": true,
  "operacoes": ["testar", "situacoes", "colaboradores", "..."] }
```

`banco` vem nulo e `banco_erro` traz o código quando o banco não abre (o canal fica de pé mesmo assim).

IABS → integrador, em resposta:

```json
{ "tipo": "bemvindo", "conexao": "Cliente (homologação)", "teste": false }
```

IABS → integrador (pedido):

```json
{ "tipo": "pedido", "id": "9b1c…", "op": "colaboradores", "params": { … } }
```

Integrador → IABS (resposta, mesmo `id`):

```json
{ "tipo": "resposta", "id": "9b1c…", "ok": true,  "dados": { … } }
{ "tipo": "resposta", "id": "9b1c…", "ok": false, "erro": { "codigo": "CLIENTE_COLUNA_OBRIGATORIA", "detalhe": { "coluna": "codmot" } } }
```

O integrador atende um pedido por vez (fila). O tempo limite é da IABS (60 s por pedido; 120 s no bloco de colaboradores).

## Tipos

- **pessoa**: `{ "numemp": 101, "tipcol": 1, "numcad": 4521 }`
- **chave** da linha da r038afa: pessoa + `"datafa": "2026-11-20"`, `"horafa": 0`
- **data**: `"AAAA-MM-DD"`. Nas respostas, datas do banco também saem assim; a data "vazia" do Senior sai como `"1900-12-31"`.
- **marca**: `IABS #35474` ou `IABS HML #35474` (regex `^IABS( [A-Z0-9]{1,8})? #\d{1,12}$`). O integrador recusa outra.

## Operações

Leitura (sempre liberadas):

| op | params | dados |
|---|---|---|
| `testar` | `{}` | `{ banco, produto, versao, tabelas: [{tabela, ok}], permissao: {incluir, alterar, excluir}, gravacao }` (permissão na r038afa; `gravacao` = liberada na janela do integrador) |
| `situacoes` | `{ empresas: [101], tipos: [1] }` | `{ linhas: [{codigo, descricao, quantidade}] }` |
| `colaboradores` | `{ empresas, tipos, setor_de: "LOCAL"\|"CENTRO_CUSTO", depois?: pessoa, cpf?: "12345678901" }` | `{ linhas: [...] }`: até 1.000, na ordem (numemp, tipcol, numcad), começando depois de `depois`; colunas: `numemp tipcol numcad numcpf nomfun datnas tipsex datadm sitafa datafa setor titcar nomfil emapar emacom dddtel numtel nmddd2 nmtel2 grains valsal hormes horsem` |
| `afastamentos` | `{ pessoa, ini, fim, marca }` | `{ linhas: [{datafa, horafa, datter, sitafa, oriafa, marca}] }`: linhas da pessoa que cruzam o período; `marca` = `"ESTA"` (a do pedido), `"OUTRA_IABS"` ou `null`. O texto do campo `obsafa` nunca sai do cliente |
| `marca_na_pessoa` | `{ pessoa, marca }` | `{ linhas: [chave] }` |
| `marca_nas_empresas` | `{ marca, empresas }` | `{ linhas: [chave] }` |
| `linha_existe` | `{ chave, marca }` | `{ existe: true }` |

Gravação (só com **"Permitir que a IABS lance os atestados"** marcado na janela do integrador; senão `GRAVACAO_DESLIGADA`). Toda gravação mexe **só em linha com a marca da IABS**:

| op | params | dados |
|---|---|---|
| `lancar` | `{ pessoa, ini, fim, situacao, atendente, marca }` | `{ afetadas }`: INSERT dia inteiro (horafa/horter 0), oriafa 2, staatu 2, contov/exmret/motalt 'N', seqreg 0, obsafa = marca |
| `atualizar` | `{ chave, marca, fim, situacao }` | `{ afetadas }`: UPDATE do datter e do sitafa da linha com a chave e a marca |
| `apagar` | `{ chave, marca }` | `{ afetadas }`: DELETE da linha com a chave e a marca |
| `trocar` | `{ apagar: [chave], nova: { pessoa, ini, fim, situacao, atendente }, marca }` | `{ afetadas }`: apaga as linhas da marca e lança a nova, numa transação |

Atualização do programa (desde a 2.1.0; sempre liberada):

| op | params | dados |
|---|---|---|
| `atualizar_integrador` | `{ versao: "2.1.1", sha256: "<64 hex>" }` | `{ situacao: "AGENDADA", versao, atual }`: o integrador deixa o pedido e liga o atualizador (serviço como SYSTEM), que confere de novo a versão, o SHA-256 da IABS contra o do GitHub (`api.github.com/repos/tecnologiahtk/IntegradorIABS/releases/tags/v<versao>`, asset `IntegradorIABS.msi`) e o do arquivo baixado, e roda o `msiexec`. Erros: `PARAMETRO_INVALIDO`, `ATUALIZACAO_DESNECESSARIA` (versão não é mais nova), `ATUALIZACAO_FALHOU` (não ligou o atualizador) |

O "ola" leva a última atualização: `"atualizacao": { "versao", "situacao": "PEDIDA" | "BAIXANDO" | "INSTALANDO" | "ERRO", "codigo", "em" }` (ou `null`). Códigos do atualizador: `VERSAO_NAO_E_MAIS_NOVA`, `VERSAO_NAO_PUBLICADA`, `VERSAO_SEM_INSTALADOR`, `GITHUB_INDISPONIVEL`, `SHA256_DIVERGENTE_GITHUB`, `URL_INESPERADA`, `ENDERECO_NAO_PERMITIDO`, `DOWNLOAD_FALHOU`, `ARQUIVO_GRANDE_DEMAIS`, `SHA256_DIVERGENTE_ARQUIVO`, `INSTALADOR_NAO_INICIOU`.

## Erros (`erro.codigo`)

| código | quando |
|---|---|
| `PARAMETRO_INVALIDO` | parâmetro fora da regra (`detalhe.campo`) |
| `OPERACAO_DESCONHECIDA` | op que esta versão não tem |
| `GRAVACAO_DESLIGADA` | gravação pedida sem a liberação na janela |
| `BANCO_LOGIN` | o banco recusou usuário ou senha (18456) |
| `BANCO_SEM_ACESSO` | entrou no servidor, mas não no banco (4060) |
| `BANCO_SEM_RESPOSTA` | servidor do banco fora do ar, porta fechada ou tempo esgotado |
| `LEITURA_RECUSADA` | sem permissão de ler a tabela (229/230 num SELECT) |
| `ESCRITA_RECUSADA` | sem permissão de gravar (229/230 num INSERT/UPDATE/DELETE) |
| `CLIENTE_CHAVE_REPETIDA` | 2627/2601 |
| `CLIENTE_COLUNA_OBRIGATORIA` | 515 (`detalhe.coluna`) |
| `CLIENTE_REFERENCIA_INVALIDA` | 547 (`detalhe.restricao`) |
| `BANCO_FALHOU` | outro erro do banco (`detalhe.numero`) |
| `INTEGRADOR_FALHOU` | erro inesperado no integrador |

A mensagem para a pessoa é escrita pela IABS. O texto do erro do banco nunca sai do cliente (pode ter dado de colaborador).

## Log no cliente

Uma linha por pedido: operação, resultado, tempo e quantidade de linhas. Nunca parâmetro, CPF, nome ou valor de coluna.
