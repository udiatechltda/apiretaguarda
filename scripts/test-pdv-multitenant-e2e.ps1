param(
    [int]$ApiPort = 5017,
    [string]$MysqlContainer = "t2ti-db-mysql",
    [string]$ComposeFile = "D:\DEVELOPER\REPOS TECH ONE\retaguardash\T2TiRetaguardaSH\docker-compose.yml",
    [string]$RetaguardaDatabase = "retaguarda_mt_test",
    [string]$RootPassword = "MySql@2025"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $repoRoot "T2TiRetaguardaSH"
$dllPath = Join-Path $projectDir "bin\Debug\net8.0\T2TiRetaguardaSH.dll"
$runStamp = Get-Date -Format "yyyyMMdd-HHmmss"
$artifactRoot = Join-Path $repoRoot "artifacts\pdv-multitenant-e2e\$runStamp"
$apiLog = Join-Path $artifactRoot "api.log"
$apiErr = Join-Path $artifactRoot "api.err.log"
$reportPath = Join-Path $artifactRoot "report.txt"
$apiProcess = $null

New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null

function Write-Report([string]$message) {
    $line = "$(Get-Date -Format HH:mm:ss) $message"
    Write-Host $line
    Add-Content -Path $reportPath -Value $line
}

function Assert-True([bool]$condition, [string]$message) {
    if (-not $condition) {
        throw "ASSERT FAIL: $message"
    }

    Write-Report "OK: $message"
}

function Invoke-Mysql([string]$sql, [switch]$Raw) {
    $args = @(
        "exec",
        "-e",
        "MYSQL_PWD=$RootPassword",
        $MysqlContainer,
        "mysql",
        "-uroot",
        "--default-character-set=utf8mb4"
    )

    if ($Raw) {
        $args += @("-N", "-B")
    }

    $args += @("-e", $sql)
    & docker @args
    if ($LASTEXITCODE -ne 0) {
        throw "mysql command failed: $sql"
    }
}

function ConvertTo-Md5Ascii([string]$value) {
    $md5 = [System.Security.Cryptography.MD5]::Create()
    try {
        $bytes = [System.Text.Encoding]::ASCII.GetBytes($value)
        return -join ($md5.ComputeHash($bytes) | ForEach-Object { $_.ToString("x2") })
    }
    finally {
        $md5.Dispose()
    }
}

function New-SnapshotRecord {
    param(
        [string]$idLocal,
        [Parameter(ValueFromRemainingArguments = $true)]
        [object[]]$dados
    )

    if ($dados.Count -ne 1 -or $dados[0] -isnot [hashtable]) {
        $tipos = ($dados | ForEach-Object { if ($null -eq $_) { "<null>" } else { $_.GetType().FullName } }) -join ", "
        throw "New-SnapshotRecord expects a single hashtable payload. Count=$($dados.Count); Types=$tipos"
    }

    $json = $dados[0] | ConvertTo-Json -Depth 50 -Compress
    return @{
        idLocal = $idLocal
        dadosJson = $json
        hash = ConvertTo-Md5Ascii $json
    }
}

function Invoke-Json([string]$method, [string]$url, [object]$body = $null, [hashtable]$headers = @{}) {
    $json = $null
    if ($null -ne $body) {
        $json = $body | ConvertTo-Json -Depth 60 -Compress
    }

    try {
        $response = Invoke-WebRequest -UseBasicParsing -Method $method -Uri $url -ContentType "application/json; charset=utf-8" -Headers $headers -Body $json -TimeoutSec 60
        $content = if ([string]::IsNullOrWhiteSpace($response.Content)) { $null } else { $response.Content | ConvertFrom-Json }
        return [pscustomobject]@{
            Status = [int]$response.StatusCode
            Body = $content
            Raw = $response.Content
        }
    }
    catch {
        $status = 0
        $raw = $_.Exception.Message
        if ($_.Exception.Response) {
            $status = [int]$_.Exception.Response.StatusCode
            $reader = [System.IO.StreamReader]::new($_.Exception.Response.GetResponseStream())
            $raw = $reader.ReadToEnd()
            $reader.Dispose()
        }

        $content = $null
        if (-not [string]::IsNullOrWhiteSpace($raw)) {
            try { $content = $raw | ConvertFrom-Json } catch { $content = $null }
        }

        return [pscustomobject]@{
            Status = $status
            Body = $content
            Raw = $raw
        }
    }
}

function New-TestAccount([string]$cnpj, [string]$nome) {
    $login = "admin"
    $senha = "Senha@123"
    $baseUrl = "http://localhost:$ApiPort"

    $create = Invoke-Json "POST" "$baseUrl/auth/criar-conta" @{
        cnpj = $cnpj
        razaoSocial = $nome
        nomeFantasia = $nome
        email = "admin@$cnpj.test"
        usuarioNome = "Administrador"
        login = $login
        senha = $senha
        perfil = "Administrador"
    }
    Assert-True ($create.Status -eq 200) "conta criada para $cnpj"

    $codigo = ConvertTo-Md5Ascii ("$cnpj`:$login" + "id#UAq2&[L5fri/GF1:2Vs5r|)z)ZU*F")
    $confirm = Invoke-Json "POST" "$baseUrl/empresa/confere-codigo-confirmacao" @{
        cnpj = $cnpj
        login = $login
    } @{ "codigo-confirmacao" = $codigo }
    Assert-True ($confirm.Status -eq 200) "codigo confirmou empresa e usuario $cnpj"

    $loginResponse = Invoke-Json "POST" "$baseUrl/auth/login" @{
        cnpj = $cnpj
        login = $login
        senha = $senha
    }
    Assert-True ($loginResponse.Status -eq 200) "login retornou token para $cnpj"
    Assert-True (-not [string]::IsNullOrWhiteSpace($loginResponse.Body.token)) "token nao vazio para $cnpj"
    Assert-True ($loginResponse.Body.empresa.bancoOperacional -eq "pdv_operacional") "login informa banco operacional unico pdv_operacional"

    $me = Invoke-Json "GET" "$baseUrl/auth/me" $null @{ Authorization = "Bearer $($loginResponse.Body.token)" }
    Assert-True ($me.Status -eq 200) "auth/me validou token de $cnpj"

    return [pscustomobject]@{
        Cnpj = $cnpj
        EmpresaId = [int]$loginResponse.Body.empresa.id
        Token = [string]$loginResponse.Body.token
        Banco = [string]$loginResponse.Body.empresa.bancoOperacional
    }
}

function Send-Snapshot([object]$account, [string]$device, [array]$tables) {
    $baseUrl = "http://localhost:$ApiPort"
    $body = @{
        dispositivoId = $device
        tabelas = $tables
    }

    $response = Invoke-Json "POST" "$baseUrl/sincroniza/pdv/snapshot" $body @{ Authorization = "Bearer $($account.Token)" }
    Assert-True ($response.Status -eq 200) "snapshot aceito para $($account.Cnpj) em $device"
    Assert-True ($response.Body.bancoOperacional -eq $account.Banco) "snapshot gravou no banco $($account.Banco)"
    return $response.Body
}

function New-FullSnapshotTables([string]$cnpj, [string]$suffix) {
    return @(
        @{ nome = "__EFMIGRATIONSHISTORY"; registros = @(New-SnapshotRecord "1" (@{ MigrationId = "x"; ProductVersion = "x" })) },
        @{ nome = "CLIENTE"; registros = @(
            (New-SnapshotRecord "1" (@{ Id = 1; Nome = "Cliente $suffix 1"; CpfCnpj = "77.000.000/0000-01"; Ativo = $true; Cep = "01.234-000" })),
            (New-SnapshotRecord "2" (@{ Id = 2; Nome = "Cliente $suffix 2"; CpfCnpj = "111.222.333-44"; Ativo = $false }))
        ) },
        @{ nome = "FORNECEDOR"; registros = @(New-SnapshotRecord "1" (@{ Id = 1; Nome = "Fornecedor $suffix"; CpfCnpj = $cnpj })) },
        @{ nome = "COLABORADOR"; registros = @(New-SnapshotRecord "1" (@{ Id = 1; Nome = "Colaborador $suffix"; Cpf = "123.456.789-09" })) },
        @{ nome = "EMPRESA"; registros = @(New-SnapshotRecord "1" (@{ Id = 1; RazaoSocial = "Empresa $suffix Snapshot"; NomeFantasia = "Fantasia $suffix"; Cnpj = $cnpj; Email = "snapshot$suffix@test.local" })) },
        @{ nome = "PRODUTO"; registros = @(
            (New-SnapshotRecord "1" (@{ Id = 1; Nome = "Produto $suffix 1"; Gtin = "789000000001"; ValorVenda = 12.34; QuantidadeEstoque = 10 })),
            (New-SnapshotRecord "2" (@{ Id = 2; Nome = "Produto $suffix 2"; Gtin = "789000000002"; ValorVenda = 23.45; QuantidadeEstoque = 20 }))
        ) },
        @{ nome = "PRODUTO_UNIDADE"; registros = @(New-SnapshotRecord "1" (@{ Id = 1; Sigla = "UN"; Descricao = "Unidade" })) },
        @{ nome = "PDV_TIPO_PAGAMENTO"; registros = @(New-SnapshotRecord "1" (@{ Id = 1; Descricao = "Dinheiro"; TeF = $false })) },
        @{ nome = "PDV_MOVIMENTO"; registros = @(New-SnapshotRecord "1" (@{ Id = 1; StatusMovimento = "A"; ValorAbertura = 100 })) },
        @{ nome = "PDV_SUPRIMENTO"; registros = @(New-SnapshotRecord "1" (@{ Id = 1; IdPdvMovimento = 1; Valor = 50 })) },
        @{ nome = "PDV_SANGRIA"; registros = @(New-SnapshotRecord "1" (@{ Id = 1; IdPdvMovimento = 1; Valor = 10 })) },
        @{ nome = "PDV_VENDA_CABECALHO"; registros = @(New-SnapshotRecord "1" (@{ Id = 1; IdPdvMovimento = 1; ValorFinal = 12.34 })) },
        @{ nome = "PDV_VENDA_DETALHE"; registros = @(New-SnapshotRecord "1" (@{ Id = 1; IdPdvVendaCabecalho = 1; IdProduto = 1; Quantidade = 1; ValorTotal = 12.34 })) },
        @{ nome = "PDV_FECHAMENTO"; registros = @(New-SnapshotRecord "1" (@{ Id = 1; IdPdvMovimento = 1; ValorFechamento = 140 })) },
        @{ nome = "COMPRA_PEDIDO_CABECALHO"; registros = @(New-SnapshotRecord "1" (@{ Id = 1; Numero = "CP-$suffix"; ValorSubtotal = 100 })) },
        @{ nome = "CONTAS_PAGAR"; registros = @(New-SnapshotRecord "1" (@{ Id = 1; Historico = "Compra $suffix"; Valor = 100 })) },
        @{ nome = "CONTAS_RECEBER"; registros = @(New-SnapshotRecord "1" (@{ Id = 1; Historico = "Venda $suffix"; Valor = 12.34 })) },
        @{ nome = "NFE CONFIG"; registros = @(New-SnapshotRecord "1" (@{ Id = 1; Serie = "1"; Ambiente = "2" })) }
    )
}

function Get-Scalar([string]$sql) {
    $value = Invoke-Mysql $sql -Raw
    $first = $value | Select-Object -First 1
    if ($null -eq $first) {
        return ""
    }

    return ([string]$first).Trim()
}

function Stop-ApiOnPort {
    $owners = Get-NetTCPConnection -LocalPort $ApiPort -State Listen -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique
    foreach ($owner in $owners) {
        Stop-Process -Id $owner -Force -ErrorAction SilentlyContinue
    }
}

try {
    Write-Report "Building retaguarda"
    dotnet build (Join-Path $repoRoot "T2TiRetaguardaSH.sln") | Tee-Object -FilePath (Join-Path $artifactRoot "build.log")
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed"
    }

    Write-Report "Ensuring MySQL container"
    docker compose -f $ComposeFile up -d db_mysql | Tee-Object -FilePath (Join-Path $artifactRoot "docker-compose.log")
    if ($LASTEXITCODE -ne 0) {
        throw "docker compose failed"
    }

    $ports = docker inspect $MysqlContainer --format "{{json .NetworkSettings.Ports}}"
    if ($ports -notmatch "HostPort") {
        Write-Report "Recreating MySQL to publish 3306"
        docker compose -f $ComposeFile up -d --force-recreate db_mysql | Tee-Object -FilePath (Join-Path $artifactRoot "docker-compose-recreate.log")
        if ($LASTEXITCODE -ne 0) {
            throw "docker compose recreate failed"
        }
    }

    $tcp = Test-NetConnection 127.0.0.1 -Port 3306
    Assert-True ($tcp.TcpTestSucceeded) "MySQL publicado em 127.0.0.1:3306"

    Stop-ApiOnPort

    $cnpj1 = "77000000000001"
    $cnpj2 = "77000000000002"
    $resetSql = @"
DROP DATABASE IF EXISTS $RetaguardaDatabase;
DROP DATABASE IF EXISTS pdv_operacional;
DROP DATABASE IF EXISTS pdv_$cnpj1;
DROP DATABASE IF EXISTS pdv_$cnpj2;
CREATE DATABASE $RetaguardaDatabase DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
"@
    Invoke-Mysql $resetSql
    Write-Report "Test databases reset"

    $defaultConnection = "Server=127.0.0.1;Port=3306;Database=$RetaguardaDatabase;Uid=root;Pwd=$RootPassword;AllowPublicKeyRetrieval=True;SslMode=None;"
    $adminConnection = "Server=127.0.0.1;Port=3306;Uid=root;Pwd=$RootPassword;AllowPublicKeyRetrieval=True;SslMode=None;"
    $startCommand = @"
`$env:ASPNETCORE_ENVIRONMENT='Development';
`$env:DOTNET_ENVIRONMENT='Development';
`$env:ASPNETCORE_URLS='http://localhost:$ApiPort';
`$env:ConnectionStrings__DefaultConnection='$defaultConnection';
`$env:ConnectionStrings__AdminConnection='$adminConnection';
Remove-Item Env:PDV_OPERACIONAL_DATABASE -ErrorAction SilentlyContinue;
Set-Location -LiteralPath '$projectDir';
& dotnet '$dllPath'
"@

    $apiProcess = Start-Process -FilePath "powershell" -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", $startCommand) -RedirectStandardOutput $apiLog -RedirectStandardError $apiErr -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 5
    $listener = Get-NetTCPConnection -LocalPort $ApiPort -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    Assert-True ($null -ne $listener) "API ouvindo em http://localhost:$ApiPort"

    $baseUrl = "http://localhost:$ApiPort"
    $invalidAccount = Invoke-Json "POST" "$baseUrl/auth/criar-conta" @{ cnpj = "123"; login = "admin"; senha = "1234" }
    Assert-True ($invalidAccount.Status -eq 400) "CNPJ invalido retorna 400"

    $unauthorized = Invoke-Json "POST" "$baseUrl/sincroniza/pdv/snapshot" @{ dispositivoId = "PDV"; tabelas = @() }
    Assert-True ($unauthorized.Status -eq 401) "snapshot sem token retorna 401"

    $account1 = New-TestAccount $cnpj1 "Empresa MT Um"
    $account2 = New-TestAccount $cnpj2 "Empresa MT Dois"

    $snap1 = Send-Snapshot $account1 "PDV-CAIXA-01" (New-FullSnapshotTables $cnpj1 "A")
    Assert-True ($snap1.totalTabelas -eq 17) "snapshot completo ignora tabela de migration e conta 17 tabelas uteis"
    $snap2 = Send-Snapshot $account2 "PDV-CAIXA-01" (New-FullSnapshotTables $cnpj2 "B")
    Assert-True ($snap2.totalRegistros -eq 19) "snapshot completo envia 19 registros uteis"

    Assert-True ((Get-Scalar "SHOW DATABASES LIKE 'pdv_operacional';") -eq "pdv_operacional") "banco operacional unico pdv_operacional criado"
    Assert-True ((Get-Scalar "SHOW DATABASES LIKE 'pdv_$cnpj1';") -eq "") "nao criou banco por CNPJ para empresa 1"
    Assert-True ((Get-Scalar "SHOW DATABASES LIKE 'pdv_$cnpj2';") -eq "") "nao criou banco por CNPJ para empresa 2"
    Assert-True ((Get-Scalar "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='pdv_operacional' AND table_name='__EFMIGRATIONSHISTORY';") -eq "0") "tabela ignorada nao foi criada"
    Assert-True ((Get-Scalar "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='pdv_operacional' AND table_name='NFE_CONFIG';") -eq "1") "nome de tabela com espaco foi normalizado"

    Assert-True ((Get-Scalar "SELECT COUNT(*) FROM pdv_operacional.CLIENTE WHERE ID_EMPRESA=$($account1.EmpresaId) AND ID_LOCAL='1' AND DISPOSITIVO_ID='PDV-CAIXA-01' AND CPF_CNPJ='77000000000001' AND CEP='01234000' AND ATIVO='S';") -eq "1") "CLIENTE normalizou CPF/CNPJ, CEP e booleano"
    Assert-True ((Get-Scalar "SELECT COUNT(*) FROM pdv_operacional.PRODUTO WHERE ID_EMPRESA=$($account1.EmpresaId);") -eq "2") "PRODUTO gravou dois registros da empresa 1"
    Assert-True ((Get-Scalar "SELECT COUNT(*) FROM pdv_operacional.CLIENTE WHERE ID_EMPRESA=$($account2.EmpresaId) AND ID_LOCAL='1';") -eq "1") "empresa 2 gravou mesmo ID_LOCAL no mesmo banco operacional"
    Assert-True ((Get-Scalar "SELECT COUNT(*) FROM pdv_operacional.CLIENTE WHERE ID_LOCAL='1' AND DISPOSITIVO_ID='PDV-CAIXA-01';") -eq "2") "mesmo ID_LOCAL e dispositivo coexistem entre empresas por ID_EMPRESA"
    Assert-True ((Get-Scalar "SELECT COUNT(*) FROM pdv_operacional.PDV_SYNC_REGISTRO WHERE ID_EMPRESA=$($account1.EmpresaId) AND DISPOSITIVO_ID='PDV-CAIXA-01';") -eq "19") "PDV_SYNC_REGISTRO guarda ID_EMPRESA e DISPOSITIVO_ID"
    Assert-True ((Get-Scalar "SELECT COUNT(*) FROM pdv_operacional.PDV_DISPOSITIVO WHERE ID_EMPRESA=$($account1.EmpresaId) AND DISPOSITIVO_ID='PDV-CAIXA-01';") -eq "1") "dispositivo registrado por empresa"
    Assert-True ((Get-Scalar "SELECT COUNT(*) FROM pdv_operacional.PDV_AUDITORIA_OPERACIONAL WHERE ID_EMPRESA=$($account1.EmpresaId);") -eq "19") "auditoria operacional criada para cada registro"
    Assert-True ((Get-Scalar "SELECT COUNT(*) FROM pdv_operacional.INTEGRACAO_OUTBOX WHERE ID_EMPRESA=$($account1.EmpresaId) AND STATUS='PROCESSADO';") -eq "19") "outbox processada para cada registro"
    Assert-True ((Get-Scalar "SELECT COUNT(*) FROM $RetaguardaDatabase.EMPRESA WHERE ID=$($account1.EmpresaId) AND RAZAO_SOCIAL='Empresa A Snapshot' AND REGISTRADO='S';") -eq "1") "snapshot de EMPRESA atualizou retaguarda administrativa"

    $updateTables = @(
        @{ nome = "CLIENTE"; registros = @(
            New-SnapshotRecord "1" (@{ Id = 1; Nome = "Cliente A Atualizado"; CpfCnpj = "77.000.000/0000-01"; Ativo = $true })
        ) }
    )
    [void](Send-Snapshot $account1 "PDV-CAIXA-01" $updateTables)
    Assert-True ((Get-Scalar "SELECT COUNT(*) FROM pdv_operacional.CLIENTE WHERE ID_EMPRESA=$($account1.EmpresaId) AND ID_LOCAL='1' AND NOME='Cliente A Atualizado' AND EXCLUIDO='N';") -eq "1") "upsert atualizou registro existente"
    Assert-True ((Get-Scalar "SELECT COUNT(*) FROM pdv_operacional.CLIENTE WHERE ID_EMPRESA=$($account1.EmpresaId) AND ID_LOCAL='2' AND EXCLUIDO='S';") -eq "1") "snapshot parcial da tabela marcou ausente como excluido"

    $device2Tables = @(
        @{ nome = "CLIENTE"; registros = @(
            New-SnapshotRecord "1" (@{ Id = 1; Nome = "Cliente A Caixa 2"; CpfCnpj = "77.000.000/0000-01" })
        ) }
    )
    [void](Send-Snapshot $account1 "PDV-CAIXA-02" $device2Tables)
    Assert-True ((Get-Scalar "SELECT COUNT(*) FROM pdv_operacional.CLIENTE WHERE ID_EMPRESA=$($account1.EmpresaId) AND ID_LOCAL='1';") -eq "2") "mesmo ID_LOCAL pode existir em dispositivos diferentes na mesma empresa"
    Assert-True ((Get-Scalar "SELECT COUNT(*) FROM pdv_operacional.PDV_SYNC_REGISTRO WHERE ID_EMPRESA=$($account1.EmpresaId) AND TABELA='CLIENTE' AND ID_LOCAL='1';") -eq "2") "registro de sync tambem separa por dispositivo"

    $badToken = Invoke-Json "GET" "$baseUrl/auth/me" $null @{ Authorization = "Bearer token-invalido" }
    Assert-True ($badToken.Status -eq 401) "token invalido retorna 401"

    Write-Report "E2E multitenant concluido com sucesso"
    Write-Report "Artifacts: $artifactRoot"
}
finally {
    Stop-ApiOnPort
    if ($apiProcess -and -not $apiProcess.HasExited) {
        Stop-Process -Id $apiProcess.Id -Force -ErrorAction SilentlyContinue
    }
}
