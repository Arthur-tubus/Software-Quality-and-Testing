using System;
using System.Linq;
using Moq;
using Npgsql;
using Xunit;

namespace Rental.Tests;

/// <summary>
/// Integration Test Suite: Service -> реальні Repository -> реальна PostgreSQL.
/// Test Double (Moq) використовується лише для IRentalPolicy — зовнішньої залежності.
///
/// Незалежність тестів: xUnit створює новий екземпляр класу для кожного тесту,
/// тому в кожного тесту свій token. Усі коди обладнання та reference містять цей token,
/// а Dispose() видаляє лише "свої" записи (спочатку оренди, потім обладнання).
/// Тести не залежать від порядку запуску і їх можна запускати повторно.
/// </summary>
public class IntegrationTests : IDisposable
{
    private readonly NpgsqlConnection connection;
    private readonly EquipmentRepository equipmentRepo;
    private readonly RentalRepository rentalRepo;
    private readonly Mock<IRentalPolicy> policy;
    private readonly RentalService service;
    private readonly string token = Guid.NewGuid().ToString("N").Substring(0, 12);
    private int counter;

    public IntegrationTests()
    {
        connection = Database.Connect();
        equipmentRepo = new EquipmentRepository(connection);
        rentalRepo = new RentalRepository(connection);

        // За замовчуванням зовнішня політика дозволяє оренду
        policy = new Mock<IRentalPolicy>();
        AllowPolicy();

        service = new RentalService(equipmentRepo, rentalRepo, policy.Object);
    }

    public void Dispose()
    {
        try
        {
            Exec("DELETE FROM rentals WHERE reference LIKE @p", token + "%");   // позиції видаляться каскадом
            Exec("DELETE FROM equipment WHERE code LIKE @p", token + "%");
        }
        finally
        {
            connection.Dispose();
        }
    }

    // ---------------------------------------------------------------------
    // Repository <-> Database
    // ---------------------------------------------------------------------

    [Fact]
    public void EquipmentRepository_CreateAndFindById_repositoryState()
    {
        var input = new Equipment(987654321, Code(), 15000, 7);

        var created = equipmentRepo.Create(input);

        Assert.True(created.Id > 0);
        Assert.NotEqual(input.Id, created.Id);          // Create ігнорує вхідний id

        var found = equipmentRepo.FindById(created.Id);
        Assert.NotNull(found);
        Assert.Equal(created, found);
        Assert.Equal(input.Code, found.Code);
        Assert.Equal(15000L, found.DailyRate);
        Assert.Equal(7, found.Stock);

        Assert.Null(equipmentRepo.FindById(long.MaxValue));   // відсутній запис -> null
    }

    [Fact]
    public void RentalRepository_CreateAndFindById_repositoryState()
    {
        var eq = NewEquipment(10000, 10);
        var input = new RentalOrder(555, Ref(), 3, 60000, 0, RentalStatus.Active,
            new RentalItem(777, eq.Id, 2, 10000));

        var created = rentalRepo.Create(input);

        Assert.True(created.Id > 0);
        Assert.NotEqual(input.Id, created.Id);
        Assert.True(created.Item.Id > 0);
        Assert.NotEqual(input.Item.Id, created.Item.Id);

        var found = rentalRepo.FindById(created.Id);
        Assert.NotNull(found);
        Assert.Equal(created, found);                    // перевіряє mapping rentals + rental_items
        Assert.Equal(RentalStatus.Active, found.Status);
        Assert.Equal(eq.Id, found.Item.EquipmentId);
        Assert.Equal(2, found.Item.Quantity);
        Assert.Equal(10000L, found.Item.DailyRate);
        Assert.Equal(60000L, found.Total);
    }

    [Fact]
    public void EquipmentRepository_Update_persistsNewValues()
    {
        var eq = NewEquipment(10000, 10);
        var updated = eq with { Code = Code(), DailyRate = 20000, Stock = 3 };

        equipmentRepo.Update(updated);

        Assert.Equal(updated, equipmentRepo.FindById(eq.Id));

        // Update відсутнього обладнання -> ArgumentException
        Assert.Throws<ArgumentException>(() =>
            equipmentRepo.Update(new Equipment(long.MaxValue, Code(), 10000, 1)));
    }

    [Fact]
    public void EquipmentRepository_DeleteEquipmentWithRental_isRestrictedByDatabase()
    {
        var eq = NewEquipment();
        NewRental(eq);

        // ON DELETE RESTRICT: обладнання з позиціями видалити не можна
        var ex = Assert.Throws<PostgresException>(() => equipmentRepo.Delete(eq.Id));
        Assert.Equal("23001", ex.SqlState);
        Assert.NotNull(equipmentRepo.FindById(eq.Id));

        // Delete відсутнього запису нічого не змінює і не кидає виняток
        equipmentRepo.Delete(long.MaxValue);
    }

    [Fact]
    public void RentalRepository_Delete_removesRentalAndItsItem()
    {
        var eq = NewEquipment();
        var rental = NewRental(eq);
        Assert.Equal(1L, Count("SELECT count(*) FROM rental_items WHERE rental_id=@p", rental.Id));

        rentalRepo.Delete(rental.Id);

        Assert.Null(rentalRepo.FindById(rental.Id));
        Assert.Equal(0L, Count("SELECT count(*) FROM rental_items WHERE rental_id=@p", rental.Id)); // каскад

        // після видалення оренди обладнання вже можна видалити
        equipmentRepo.Delete(eq.Id);
        Assert.Null(equipmentRepo.FindById(eq.Id));
    }

    [Fact]
    public void RentalRepository_FindRentals_filtersByEquipmentAndStatus()
    {
        var a = NewEquipment();
        var b = NewEquipment();
        var r1 = NewRental(a);
        var r2 = NewRental(a);
        var r3 = NewRental(a);
        NewRental(b);
        rentalRepo.MarkReturned(r2.Id, 0);

        var active = rentalRepo.FindRentals(a.Id, RentalStatus.Active);
        var returned = rentalRepo.FindRentals(a.Id, RentalStatus.Returned);

        Assert.Equal(new[] { r1.Id, r3.Id }, active.Select(r => r.Id).ToArray());   // обидві умови + order by id
        Assert.Equal(new[] { r2.Id }, returned.Select(r => r.Id).ToArray());
    }

    // ---------------------------------------------------------------------
    // Service -> Repository -> Database
    // ---------------------------------------------------------------------

    [Fact]
    public void Rent_validRequest_createsActiveRental_serviceState()
    {
        var eq = NewEquipment(10000, 10);

        var order = service.Rent(new RentalRequest(Ref(), eq.Id, 2, 3));

        Assert.True(order.Id > 0);
        Assert.Equal(RentalStatus.Active, order.Status);
        Assert.Equal(60000L, order.Total);               // 10000 * 2 * 3
        Assert.Equal(0L, order.LateFee);
        Assert.Equal(2, order.Item.Quantity);
        Assert.Equal(10000L, order.Item.DailyRate);

        // зовнішня залежність викликана рівно один раз з правильними аргументами
        policy.Verify(p => p.IsAllowed(It.Is<Equipment>(e => e.Id == eq.Id), 2, 3), Times.Once);
        policy.VerifyNoOtherCalls();
    }

    [Fact]
    public void Rent_policyDenies_createsNoRecord_serviceState()
    {
        var eq = NewEquipment();
        var reference = Ref();
        policy.Setup(p => p.IsAllowed(It.IsAny<Equipment>(), It.IsAny<int>(), It.IsAny<int>())).Returns(false);

        Assert.Throws<RentalRejectedException>(() => service.Rent(new RentalRequest(reference, eq.Id, 2, 3)));

        // БД не змінена: ні оренди, ні позиції
        Assert.Equal(0L, Count("SELECT count(*) FROM rentals WHERE reference=@p", reference));
        Assert.Equal(0L, Count("SELECT count(*) FROM rental_items WHERE equipment_id=@p", eq.Id));
        Assert.Empty(rentalRepo.FindRentals(eq.Id, RentalStatus.Active));
        policy.Verify(p => p.IsAllowed(It.IsAny<Equipment>(), It.IsAny<int>(), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public void Rent_validRequest_persistsRentalAndKeepsPriceSnapshot_persistedState()
    {
        var eq = NewEquipment(10000, 10);
        var order = service.Rent(new RentalRequest(Ref(), eq.Id, 2, 3));

        // повторне читання через Repository
        var saved = rentalRepo.FindById(order.Id);
        Assert.NotNull(saved);
        Assert.Equal(order, saved);
        Assert.Equal(RentalStatus.Active, saved.Status);
        Assert.Equal(60000L, saved.Total);

        // залишок лише перевіряється, не зменшується
        Assert.Equal(10, equipmentRepo.FindById(eq.Id).Stock);

        // ціна зафіксована в позиції: зміна обладнання не впливає на оренду
        equipmentRepo.Update(eq with { DailyRate = 50000 });
        var after = rentalRepo.FindById(order.Id);
        Assert.Equal(10000L, after.Item.DailyRate);
        Assert.Equal(60000L, after.Total);
    }

    // Персональне правило варіанта: доплата за прострочення
    // 2 одиниці по 100 грн (10000 коп.) на 3 дні -> початкова сума 60000 коп.
    [Theory]
    [InlineData(0, 0L)]        // немає прострочення
    [InlineData(1, 0L)]        // перший день безкоштовний
    [InlineData(2, 2000L)]     // 10% від 20000 за 1 платний день
    [InlineData(4, 6000L)]     // приклад із README: 60 грн
    [InlineData(31, 60000L)]   // рівно максимум = початкова сума
    [InlineData(32, 60000L)]   // обмежено початковою сумою
    public void ReturnRental_lateFeeRule_persistsFee(int overdueDays, long expectedFee)
    {
        var eq = NewEquipment(10000, 10);
        var order = service.Rent(new RentalRequest(Ref(), eq.Id, 2, 3));

        var returned = service.ReturnRental(order.Id, overdueDays);

        Assert.Equal(expectedFee, returned.LateFee);

        // фактичний стан БД
        var saved = rentalRepo.FindById(order.Id);
        Assert.Equal(RentalStatus.Returned, saved.Status);
        Assert.Equal(expectedFee, saved.LateFee);
        Assert.Equal(60000L, saved.Total);               // початкова сума не змінюється
        Assert.Empty(rentalRepo.FindRentals(eq.Id, RentalStatus.Active));
        Assert.Single(rentalRepo.FindRentals(eq.Id, RentalStatus.Returned));

        // при поверненні зовнішня залежність не викликається (був лише виклик при Rent)
        policy.Verify(p => p.IsAllowed(It.IsAny<Equipment>(), It.IsAny<int>(), It.IsAny<int>()), Times.Once);
    }

    [Fact]
    public void ReturnRental_fractionalLateFee_isRoundedDown()
    {
        var eq = NewEquipment(105, 10);                  // 105 коп. на добу
        var order = service.Rent(new RentalRequest(Ref(), eq.Id, 1, 2));   // total = 210

        service.ReturnRental(order.Id, 2);               // 105 * 1 * 1 / 10 = 10.5 -> 10

        var saved = rentalRepo.FindById(order.Id);
        Assert.Equal(210L, saved.Total);
        Assert.Equal(10L, saved.LateFee);
    }

    [Fact]
    public void ReturnRental_alreadyReturned_isRejectedAndKeepsState()
    {
        var eq = NewEquipment(10000, 10);
        var order = service.Rent(new RentalRequest(Ref(), eq.Id, 2, 3));
        service.ReturnRental(order.Id, 4);               // доплата 6000

        Assert.Throws<RentalRejectedException>(() => service.ReturnRental(order.Id, 31));

        var saved = rentalRepo.FindById(order.Id);       // стан після першого повернення не змінився
        Assert.Equal(RentalStatus.Returned, saved.Status);
        Assert.Equal(6000L, saved.LateFee);
    }

    [Fact]
    public void Rent_insufficientStock_isRejectedWithoutPolicyCall()
    {
        var eq = NewEquipment(10000, 1);
        var reference = Ref();

        Assert.Throws<RentalRejectedException>(() => service.Rent(new RentalRequest(reference, eq.Id, 2, 3)));

        Assert.Equal(0L, Count("SELECT count(*) FROM rentals WHERE reference=@p", reference));
        Assert.Equal(0L, Count("SELECT count(*) FROM rental_items WHERE equipment_id=@p", eq.Id));
        policy.Verify(p => p.IsAllowed(It.IsAny<Equipment>(), It.IsAny<int>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public void Rent_policyThrows_createsNoRecord()
    {
        var eq = NewEquipment();
        var reference = Ref();
        policy.Setup(p => p.IsAllowed(It.IsAny<Equipment>(), It.IsAny<int>(), It.IsAny<int>()))
              .Throws(new InvalidOperationException("policy unavailable"));

        Assert.Throws<InvalidOperationException>(() => service.Rent(new RentalRequest(reference, eq.Id, 2, 3)));

        Assert.Equal(0L, Count("SELECT count(*) FROM rentals WHERE reference=@p", reference));
        Assert.Equal(0L, Count("SELECT count(*) FROM rental_items WHERE equipment_id=@p", eq.Id));
    }

    [Fact]
    public void Rent_duplicateReference_leavesOnlyFirstRental()
    {
        var eq = NewEquipment(10000, 10);
        var reference = Ref();
        var first = service.Rent(new RentalRequest(reference, eq.Id, 1, 1));

        var ex = Assert.Throws<PostgresException>(() =>
            service.Rent(new RentalRequest(reference, eq.Id, 2, 3)));
        Assert.Equal("23505", ex.SqlState);              // unique_violation

        var active = rentalRepo.FindRentals(eq.Id, RentalStatus.Active);
        Assert.Single(active);
        Assert.Equal(first.Id, active[0].Id);
        Assert.Equal(1L, Count("SELECT count(*) FROM rental_items WHERE equipment_id=@p", eq.Id));
    }

    // ---------------------------------------------------------------------
    // Допоміжні методи
    // ---------------------------------------------------------------------

    private void AllowPolicy() =>
        policy.Setup(p => p.IsAllowed(It.IsAny<Equipment>(), It.IsAny<int>(), It.IsAny<int>())).Returns(true);

    private string Code() => $"{token}-E{++counter}";
    private string Ref() => $"{token}-R{++counter}";

    private Equipment NewEquipment(long rate = 10000, int stock = 10) =>
        equipmentRepo.Create(new Equipment(0, Code(), rate, stock));

    private RentalOrder NewRental(Equipment eq, int quantity = 2, int days = 3) =>
        rentalRepo.Create(new RentalOrder(0, Ref(), days, eq.DailyRate * quantity * days, 0,
            RentalStatus.Active, new RentalItem(0, eq.Id, quantity, eq.DailyRate)));

    private void Exec(string sql, object value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("p", value);
        command.ExecuteNonQuery();
    }

    private long Count(string sql, object value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("p", value);
        return Convert.ToInt64(command.ExecuteScalar());
    }
}