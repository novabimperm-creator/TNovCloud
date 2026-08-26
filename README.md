# TNovCloud

Среда общих данных: файловый архив с major-версиями, диск в Проводнике Windows и последовательные согласования.

## Состав

| Проект | Назначение |
|---|---|
| `src/TNovCloud.Api` | Сервер ASP.NET на Linux, PostgreSQL, хранение файлов |
| `src/TNovCloud.Client` | Windows-клиент: диск **TNovCloud** в Проводнике |
| `src/TNovCloud.Shared` | Общие DTO |

Версии файлов — только major: 1, 2, 3… Имя файла не меняется. Откат создаёт новую версию с содержимым старой.

## Сервер (Linux)

Нужны PostgreSQL 16 и .NET 10 (или Docker).

```bash
export ConnectionStrings__Postgres="Host=db;Port=5432;Database=tnovcloud;Username=tnovcloud;Password=tnovcloud"
export Jwt__Key="длинный-секрет-не-меньше-32-символов"
export Storage__Root=/var/tnovcloud/data
dotnet publish src/TNovCloud.Api -c Release -o /opt/tnovcloud
```

Через Docker Compose (из корня репозитория):

```bash
docker compose up --build -d
```

API: `http://<сервер>:5080/health`

Стартовые пользователи:

- `admin` / `admin`
- `designer` / `designer`

После первого запуска смените пароли и `Jwt__Key`.

## Windows-клиент

```powershell
dotnet run --project src/TNovCloud.Client
```

После входа появляется диск **TNovCloud** (по умолчанию `T:`). В контекстном меню файла:

- Взять на редактирование
- Сохранить изменения (новая major-версия)
- Отменить изменения
- Отправить на согласование
- Свойства и версии

Файлы, которые вы не редактируете, на диске только для чтения. Пока файл на согласовании, checkout запрещён. Если все согласующие подтвердили — статус файла становится `Approved`.

Скопированный в диск новый файл при синхронизации загружается на сервер как версия 1.

## Локальная разработка

PostgreSQL должен быть доступен по строке из `src/TNovCloud.Api/appsettings.json`. Затем:

```powershell
dotnet run --project src/TNovCloud.Api
dotnet run --project src/TNovCloud.Client
```
