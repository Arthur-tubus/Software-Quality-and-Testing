using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Rental.Integration;

/// <summary>
/// Піднімає PostgreSQL і WireMock у контейнерах один раз на клас тестів.
/// Порти й облікові дані беруться з runtime-контейнера (динамічний port mapping).
/// </summary>
public sealed class InfraFixture : IAsyncLifetime
{
    private const int WireMockPort = 8080;

    public PostgreSqlContainer Postgres { get; } = new PostgreSqlBuilder()
        .WithImage("postgres:17.6-alpine")
        .Build();

    public IContainer WireMock { get; } = new ContainerBuilder()
        .WithImage("wiremock/wiremock:3.9.1")
        .WithPortBinding(WireMockPort, true) // true = випадковий вільний порт на хості
        .WithWaitStrategy(Wait.ForUnixContainer()
            .UntilHttpRequestIsSucceeded(r => r.ForPort(WireMockPort).ForPath("/__admin/health")))
        .Build();

    public string ConnectionString => Postgres.GetConnectionString();

    public Uri WireMockUrl =>
        new($"http://{WireMock.Hostname}:{WireMock.GetMappedPublicPort(WireMockPort)}/");

    public async Task InitializeAsync()
    {
        await Task.WhenAll(Postgres.StartAsync(), WireMock.StartAsync());
        await ApplyMigrationsAsync();
    }

    
    private async Task ApplyMigrationsAsync()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "migrations");
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        foreach (var file in Directory.GetFiles(dir, "V*.sql").OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            await using var command = new NpgsqlCommand(await File.ReadAllTextAsync(file), connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync()
    {
        await Postgres.DisposeAsync();
        await WireMock.DisposeAsync();
    }
}

public class RentalIntegrationTests : IClassFixture<InfraFixture>, IAsyncLifetime
{
    private readonly InfraFixture infra;
    private readonly HttpClient admin = new();
    private NpgsqlConnection connection = null!;
    private HttpRentalPolicy policy = null!;
    private EquipmentRepository equipment = null!;
    private RentalRepository rentals = null!;
    private RentalService service = null!;
    private Equipment item = null!;

    public RentalIntegrationTests(InfraFixture infra)
    {
        this.infra = infra;
        admin.BaseAddress = infra.WireMockUrl;
    }

    // xUnit створює новий екземпляр класу на кожен тест.
    public async Task InitializeAsync()
    {
        connection = new NpgsqlConnection(infra.ConnectionString);
        await connection.OpenAsync();

        
        await using (var clean = new NpgsqlCommand(
            "TRUNCATE rental_items, rentals, equipment RESTART IDENTITY CASCADE", connection))
            await clean.ExecuteNonQueryAsync();

        
        (await admin.PostAsync("/__admin/reset", null)).EnsureSuccessStatusCode();

        
        equipment = new EquipmentRepository(connection);
        rentals = new RentalRepository(connection);
        policy = new HttpRentalPolicy(infra.WireMockUrl, TimeSpan.FromSeconds(5));
        service = new RentalService(equipment, rentals, policy);
        item = equipment.Create(new Equipment(0, "CAMERA-1", 10000, 5));
    }

    public async Task DisposeAsync()
    {
        policy.Dispose();
        admin.Dispose();
        await connection.DisposeAsync();
    }

    // ---------- сценарії ----------

    [Fact(DisplayName = "Дозволена оренда зберігається в PostgreSQL")]
    public async Task Rent_Allowed_PersistsRental()
    {
        await StubPolicy(200, """{"allowed":true}""");

        var result = service.Rent(new RentalRequest("REF-OK", item.Id, 2, 3));

        Assert.Equal(RentalStatus.Active, result.Status);

        // перевірка фактичного стану окремим підключенням (не через об'єкт, який повернув Service)
        var row = await QueryRow("SELECT reference, days, total, late_fee, status FROM rentals");
        Assert.Equal(new object[] { "REF-OK", 3, 60000L, 0L, "Active" }, row);
        var line = await QueryRow("SELECT equipment_id, quantity, daily_rate FROM rental_items");
        Assert.Equal(new object[] { item.Id, 2, 10000L }, line);

        var calls = await PolicyRequests();
        var call = Assert.Single(calls);
        AssertPolicyCall(call, item.Id, 2, 3);
    }

    [Fact(DisplayName = "Зовнішня відмова (allowed=false): запису немає, HTTP-запит = 1")]
    public async Task Rent_ExternalRejection_NothingPersisted()
    {
        await StubPolicy(200, """{"allowed":false}""");

        Assert.Throws<RentalRejectedException>(() =>
            service.Rent(new RentalRequest("REF-DENIED", item.Id, 2, 3)));

        await AssertNothingPersisted();
        AssertPolicyCall(Assert.Single(await PolicyRequests()), item.Id, 2, 3);
    }

    [Fact(DisplayName = "Сервіс недоступний (503): оренда не створюється, HTTP-запит = 1")]
    public async Task Rent_PolicyUnavailable_NothingPersisted()
    {
        await StubPolicy(503, "");

        Assert.Throws<RentalPolicyException>(() =>
            service.Rent(new RentalRequest("REF-503", item.Id, 2, 3)));

        await AssertNothingPersisted();
        AssertPolicyCall(Assert.Single(await PolicyRequests()), item.Id, 2, 3);
    }

    [Fact(DisplayName = "Локальна відмова (нестача обладнання): HTTP-запит = 0")]
    public async Task Rent_LocalRejection_NoHttpCall()
    {
        // WireMock дозволив би оренду, але до нього не повинні дійти
        await StubPolicy(200, """{"allowed":true}""");

        Assert.Throws<RentalRejectedException>(() =>
            service.Rent(new RentalRequest("REF-LOCAL", item.Id, 6, 3))); // stock = 5

        await AssertNothingPersisted();
        Assert.Empty(await PolicyRequests());
    }

    [Fact(DisplayName = "Персональне правило: штраф за прострочення зберігається в БД")]
    public async Task Return_AppliesLateFeeRule()
    {
        await StubPolicy(200, """{"allowed":true}""");
        var rented = service.Rent(new RentalRequest("REF-LATE", item.Id, 2, 3));

        service.ReturnRental(rented.Id, 4); 

        var row = await QueryRow("SELECT status, late_fee FROM rentals WHERE id = " + rented.Id);
        Assert.Equal(new object[] { "Returned", 6000L }, row);
    }

    // ---------- helpers ----------

    private async Task StubPolicy(int status, string body)
    {
        var mapping = new
        {
            request = new { method = "POST", url = "/rental-policy" },
            response = new
            {
                status,
                headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                body
            }
        };
        var response = await admin.PostAsync("/__admin/mappings",
            new StringContent(JsonSerializer.Serialize(mapping), Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    private async Task<List<JsonElement>> PolicyRequests()
    {
        using var doc = JsonDocument.Parse(await admin.GetStringAsync("/__admin/requests"));
        return doc.RootElement.GetProperty("requests").EnumerateArray()
            .Select(r => r.GetProperty("request").Clone()).ToList();
    }

    private static void AssertPolicyCall(JsonElement request, long equipmentId, int quantity, int days)
    {
        Assert.Equal("POST", request.GetProperty("method").GetString());
        Assert.Equal("/rental-policy", request.GetProperty("url").GetString());
        using var body = JsonDocument.Parse(request.GetProperty("body").GetString()!);
        Assert.Equal(equipmentId, body.RootElement.GetProperty("equipmentId").GetInt64());
        Assert.Equal(quantity, body.RootElement.GetProperty("quantity").GetInt32());
        Assert.Equal(days, body.RootElement.GetProperty("days").GetInt32());
    }

    private async Task AssertNothingPersisted()
    {
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM rentals"));
        Assert.Equal(0L, await Scalar("SELECT count(*) FROM rental_items"));
        Assert.Equal(5L, await Scalar("SELECT stock FROM equipment WHERE id = " + item.Id)); // залишок не змінився
    }

    private async Task<long> Scalar(string sql)
    {
        await using var c = new NpgsqlConnection(infra.ConnectionString); // незалежне підключення
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task<object[]> QueryRow(string sql)
    {
        await using var c = new NpgsqlConnection(infra.ConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "Очікувався рівно один запис");
        var values = new object[reader.FieldCount];
        reader.GetValues(values);
        Assert.False(await reader.ReadAsync(), "Знайдено більше одного запису");
        return values;
    }
}
