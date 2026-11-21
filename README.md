# RoyaltyFlow

Учебный проект по учёту музыкальных роялти для собеседования Senior .NET Developer. В решении есть доменный расчёт, генератор CSV, PostgreSQL-хранилище, импорт и HTTP API.

## Запуск на Windows

Требуются .NET SDK 8.0.428 и, при работе через IDE, Visual Studio 2022 с поддержкой .NET 8. Сборка, обычные тесты, Swagger и liveness доступны без PostgreSQL.

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build --no-restore
dotnet run --project RoyaltyFlow.Api
```

Профиль Development слушает `http://localhost:5080`. Swagger доступен на `/swagger`, OpenAPI JSON — на `/swagger/v1/swagger.json`. `/health/live` проверяет HTTP-процесс, `/health/ready` проверяет доступность PostgreSQL и наличие применённых миграций. `/health` сохраняет прежнюю семантику liveness.

## Подключение и миграции

Для операций с данными используйте предоставленный вам экземпляр PostgreSQL 16 и отдельную пустую БД. Скопируйте фактическую строку подключения из менеджера секретов в переменную среды текущего PowerShell. `.env.example` показывает имена переменных. ASP.NET Core читает переменные процесса, файл `.env` автоматически не загружается.

```powershell
$databaseCredentials = Get-Credential -Message 'PostgreSQL account'
$db = [System.Data.Common.DbConnectionStringBuilder]::new()
$db['Host'] = Read-Host 'PostgreSQL host'
$db['Database'] = 'royaltyflow'
$db['Username'] = $databaseCredentials.UserName
$db['Password'] = $databaseCredentials.GetNetworkCredential().Password
$db['SSL Mode'] = 'VerifyFull'
$env:ConnectionStrings__RoyaltyFlow = $db.ConnectionString
dotnet run --project RoyaltyFlow.Api --no-launch-profile -- --migrate
if ($LASTEXITCODE -ne 0) { throw 'RoyaltyFlow migration failed' }
dotnet run --project RoyaltyFlow.Api
```

У миграционной роли должны быть права на создание объектов и расширения `btree_gist`, либо расширение заранее устанавливает администратор. Для ручной проверки DDL см. [schema.sql](docs/schema.sql). Для управляемой EF схемы применяйте миграцию, которая записывает версию в `__EFMigrationsHistory`. Приложение применяет миграцию только при явном вызове `--migrate`. Для обычной работы администратор может выдать отдельной роли права на чтение, вставку, обновление записей импорта и создание временных таблиц.

## Каталог и импорт

Swagger и health endpoints открываются без входа. Для GET к `/api` используйте Basic с логином `demo` и выданным демо-паролем. В Swagger выберите **Authorize** и **DemoBasic**. Для записи используйте отдельный **OperatorBearer**. Операторский токен также разрешает чтение. Публикуйте только синтетические данные в демонстрационной базе и используйте HTTPS для удалённого доступа.

Для локального запуска API передайте `DemoAccess__Password` и `DemoAccess__WriteToken` через окружение процесса. Следующий пример использует операторский токен из переменной `ROYALTYFLOW_WRITE_TOKEN`.

```powershell
$base = 'http://localhost:5080'
$headers = @{ Authorization = "Bearer $env:ROYALTYFLOW_WRITE_TOKEN" }
function Post-RoyaltyJson($path, $value) {
    Invoke-RestMethod "$base$path" -Headers $headers -Method Post -ContentType 'application/json' -Body ($value | ConvertTo-Json -Depth 6)
}
$artist = Post-RoyaltyJson '/api/artists' @{ name = 'Demo Artist' }
$holder = Post-RoyaltyJson '/api/rights-holders' @{ name = 'Demo Rights' }
$trackId = [guid]::NewGuid()
$track = Post-RoyaltyJson '/api/tracks' @{ id = $trackId; artistId = $artist.id; title = 'Demo Track' }
Post-RoyaltyJson '/api/contracts' @{
    trackId = $track.id; validFrom = '2025-01-01'; validTo = '2026-01-01'
    shares = @(@{ rightsHolderId = $holder.id; percentage = 100 })
}
$csv = "EventId,TrackId,Platform,Country,UsageDate,PlayCount`n$([guid]::NewGuid()),$trackId,DemoMusic,US,2025-01-01,10`n"
$csv | Set-Content -Encoding utf8 data/demo-import.csv
Invoke-RestMethod "$base/api/imports/preview" -Headers $headers -Method Post -ContentType 'text/csv' -InFile data/demo-import.csv
Invoke-RestMethod "$base/api/imports/upload" -Headers $headers -Method Post -ContentType 'text/csv' -InFile data/demo-import.csv
Invoke-RestMethod "$base/api/usage?trackId=$trackId&from=2025-01-01&to=2025-01-31&page=1&pageSize=50" -Headers $headers
Invoke-RestMethod "$base/api/royalties/summary?from=2025-01-01&to=2025-01-31" -Headers $headers
```

CSV отправляется непосредственно в теле запроса с `Content-Type: text/csv`. Максимум 128 МиБ. Результат первого upload — `201` с `Location`, повтор завершённого файла — `200` с существующим результатом. Пагинация списков начинается с `page=1`, принимает `pageSize` от 1 до 200 и возвращает `hasNext`. Даты в фильтрах использования и отчёта включительные. Отчёт показывает по треку число событий, сумму прослушиваний и сумму фактических начислений за период с сортировкой по ID трека.

## Синтетические данные и отдельные интеграционные тесты

```powershell
dotnet run --project RoyaltyFlow.CsvGenerator -- --count 100 --output data/sample-small.csv --seed 42
```

Образец содержит синтетические TrackId. Для успешного импорта зарегистрируйте нужные TrackId через API либо используйте минимальный пример выше. Иначе строки с отсутствующими треками будут учтены как invalid.

Тесты PostgreSQL находятся в отдельном проекте за пределами solution. При отдельном запуске Testcontainers требуется доступный Docker-совместимый сервис, который может быть удалённым. Обычная сборка и тесты решения не запускают контейнеры.

```powershell
dotnet test RoyaltyFlow.PostgresTests/RoyaltyFlow.PostgresTests.csproj
```

## Развёртывание RoyaltyFlow на VPS Fornex

### Публикация из Windows PowerShell

Для публикации закоммиченного `main` запустите в обычном Windows PowerShell:

```powershell
& 'E:\RoyaltyFlow\scripts\deploy-fornex.ps1'
```

Скрипт проверяет чистый checkout, собирает solution и запускает тесты. После проверки VPS он отправляет проверенный коммит в GitHub и передаёт архив того же коммита на VPS. Для GitHub используется существующая авторизация Git. SSH использует ключ `C:\Users\Alex\.ssh\fornex_codex` и проверяет ключ VPS по `C:\Users\Alex\.ssh\known_hosts`. В [GitHub Actions](.github/workflows/royaltyflow.yml) выполняются тесты и сборка образа.

В существующем `/opt/royaltyflow/deploy/.env` с правами `0600` используются `POSTGRES_PASSWORD`, `ROYALTYFLOW_DEMO_PASSWORD` и отдельный случайный `ROYALTYFLOW_WRITE_TOKEN`. VPS собирает образ с тегом SHA коммита и сохраняет релиз в `/opt/royaltyflow-releases`. Перед заменой API скрипт проверяет возможность отката и создаёт проверенный дамп базы `royaltyflow`. Миграции применяются при наличии ожидающих. При сбое health или smoke на прежней схеме восстанавливаются предыдущий образ и его конфигурация. Если схема могла измениться, для восстановления используются сохранённые образ, конфигурация и дамп после проверки совместимости. Откат к версии до Demo Authentication возвращает её правила доступа.

Параметр `-PreflightOnly` проверяет VPS и возможность отката. `-DiagnoseMigration` показывает сохранённую сводку и читает метаданные PostgreSQL. Smoke после публикации проверяет 100 событий под демо-учётной записью через публичный HTTPS без записи в базу.

### Ручной запуск

Выполняйте команды из корня отдельной копии RoyaltyFlow на VPS. Compose использует проект `royaltyflow` и подключает сервис `royaltyflow-api` к существующей сети `securities-repository_default`. API слушает в контейнере порт 8080, с VPS доступен на `http://127.0.0.1:18080`. Убедитесь, что этот порт свободен. PostgreSQL доступен по имени `securities-repository-postgres-1` внутри сети. Для этого сценария используются база `royaltyflow` и роль `royaltyflow_user`. Проверьте, что роль может подключаться к базе, создавать объекты в схеме `public` и временные таблицы. Если `btree_gist` ещё отсутствует, потребуется право `CREATE` на базе или предварительная установка расширения администратором в этой базе.

### Конфигурация, миграция и запуск

Создайте защищённый локальный файл настроек один раз. Если файл уже существует, проверьте его права и отредактируйте на месте.

```bash
if [ ! -e deploy/.env ]; then
  install -m 600 deploy/.env.example deploy/.env
fi
chmod 600 deploy/.env
${EDITOR:-vi} deploy/.env
```

Укажите выданный пароль в `POSTGRES_PASSWORD`, демо-пароль в `ROYALTYFLOW_DEMO_PASSWORD` и отдельный токен в `ROYALTYFLOW_WRITE_TOKEN`. Для буквального значения с символами `$`, `#` или пробелами используйте одинарные кавычки. Апостроф внутри значения записывается как `\'`. Не экспортируйте одноимённые переменные в текущий shell, иначе они перекроют файл. Используйте `docker compose config -q` для проверки без вывода разрешённой конфигурации. Пользователи с доступом к Docker могут прочитать окружение контейнера.

Следующий блок использует отдельный тег образа для релиза. Перед миграцией сделайте резервную копию по инструкции ниже и проверьте изменения схемы. Текущая миграция создаёт объекты. Изменения, удаляющие данные, требуют отдельного согласования.

```bash
set -e
export ROYALTYFLOW_TAG=task3
docker network inspect securities-repository_default --format '{{.Name}}'
docker compose --env-file deploy/.env -f deploy/docker-compose.yml config -q
docker compose --env-file deploy/.env -f deploy/docker-compose.yml build royaltyflow-api
docker compose --env-file deploy/.env -f deploy/docker-compose.yml run --rm --no-deps royaltyflow-api --migrate
docker compose --env-file deploy/.env -f deploy/docker-compose.yml up -d --no-deps --no-build royaltyflow-api
curl -fsS http://127.0.0.1:18080/health/live
curl -fsS http://127.0.0.1:18080/health/ready
```

Команда миграции сначала подтверждает имя открытой базы и версию сервера PostgreSQL 16, затем под блокировкой выполняет `MigrateAsync` и завершается. Обычный запуск API миграции не применяет. Контейнерный healthcheck проверяет liveness встроенным .NET-клиентом. Для проверки зависимостей используйте readiness. Контейнерный профиль использует стандартный режим TLS Npgsql `Prefer` для существующей Docker-сети. Если на VPS требуется проверка сертификата, согласуйте доверенный сертификат и режим до запуска.

### Проверка импорта

Оба скрипта используют стандартную библиотеку Python 3. Полный сценарий получает токен из переменной процесса `ROYALTYFLOW_WRITE_TOKEN` и добавляет синтетический каталог, договоры, события и начисления. Повторный запуск использует тот же справочник и результат импорта. Для ранее загруженного образца можно задать `ROYALTYFLOW_DEMO_PASSWORD` и выполнить `bash scripts/smoke-test.sh http://127.0.0.1:18080 --read-only` или передать `-ReadOnly` в PowerShell. Если образец импортировали без договоров или с другими правилами, скрипт завершится ошибкой сверки.

```bash
bash scripts/smoke-test.sh http://127.0.0.1:18080
```

```powershell
.\scripts\smoke-test.ps1 -BaseUrl http://127.0.0.1:18080
.\scripts\smoke-test.ps1 -CheckFixture
```

Если политика Windows требует подписи PowerShell-скриптов, тот же сценарий можно запустить командой `py -3 scripts/smoke_test.py http://127.0.0.1:18080`.

Скрипт ожидает 100 уникальных событий и ровно рассчитанные суммы по 91 треку. Для каждого трека он сверяет список событий через `/api/usage` и проверяет количество событий, прослушивания и начисления через `/api/royalties/summary`. Повтор файла должен вернуть тот же ID импорта и ту же статистику.

### Резервная копия базы royaltyflow

Запустите отдельный контейнер клиента PostgreSQL в существующей сети. Пароль вводится по запросу `pg_dump`. Файл сохраняется в закрытом каталоге на VPS. Подставьте выданные имя базы и роль, если администратор назначил отдельную роль для чтения резервных копий. Целевая база всегда `royaltyflow`.

```bash
backup_dir="$HOME/royaltyflow-backups"
install -d -m 700 "$backup_dir"
backup_file="royaltyflow-$(date -u +%Y%m%dT%H%M%SZ).dump"
docker run --rm -it --network securities-repository_default \
  --user "$(id -u):$(id -g)" --mount "type=bind,src=$backup_dir,dst=/backup" \
  --entrypoint pg_dump postgres:16.16 -h securities-repository-postgres-1 \
  -U royaltyflow_user -W -d royaltyflow -Fc -f "/backup/$backup_file"
docker run --rm --user "$(id -u):$(id -g)" \
  --mount "type=bind,src=$backup_dir,dst=/backup,readonly" \
  --entrypoint pg_restore postgres:16.16 --list "/backup/$backup_file" >/dev/null
```

Проверка списка подтверждает читаемость архива. Полноту резервной копии проверяют восстановлением в отдельную тестовую базу.

### Откат только API

Остановить текущий API можно командой `docker compose --env-file deploy/.env -f deploy/docker-compose.yml stop royaltyflow-api`. Для возврата к предыдущему совместимому релизу сохраните его образ и укажите фактический старый тег.

```bash
export ROYALTYFLOW_TAG=previous-release
docker compose --env-file deploy/.env -f deploy/docker-compose.yml up -d --no-deps --no-build --pull never royaltyflow-api
curl -fsS http://127.0.0.1:18080/health/ready
```

Откат образа оставляет базу на текущей версии схемы. Проверьте совместимость предыдущего образа со схемой до запуска. Отдельный откат схемы или восстановление данных требует согласованного плана и проверенной резервной копии. Внешняя сеть и проекты SecuritiesRepository и OrderFlow не участвуют в этих операциях.

- [Архитектура](docs/ARCHITECTURE.md)
- [Домен и расчёт](docs/DOMAIN.md)
- [Дорожная карта](docs/ROADMAP.md)
- [Решения](docs/DECISIONS.md)
