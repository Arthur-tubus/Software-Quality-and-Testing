using System;
using Moq;
using Xunit;
using UniversityTesting.Variant11;

namespace UniversityTesting.Tests
{
    public class SubscriptionServiceUnitTests
    {
        private readonly Mock<SubscriptionService.ISubscriptionState> _subscriptionStateMock;
        private readonly Mock<SubscriptionService.INonprofitRegistry> _nonprofitRegistryMock;
        private readonly Mock<SubscriptionService.IBillingRepository> _billingRepositoryMock;
        private readonly SubscriptionService _service;
        
        #region Lab 2

        public SubscriptionServiceUnitTests()
        {
            _subscriptionStateMock = new Mock<SubscriptionService.ISubscriptionState>();
            _nonprofitRegistryMock = new Mock<SubscriptionService.INonprofitRegistry>();
            _billingRepositoryMock = new Mock<SubscriptionService.IBillingRepository>();

            _service = new SubscriptionService(
                _subscriptionStateMock.Object,
                _nonprofitRegistryMock.Object,
                _billingRepositoryMock.Object
            );
        }

        [Fact]// Тест звичайного розрахунку для активної підписки
        public void Process_ActiveSubscriptionStandardCalculation_ReturnsCorrectStateAndTotal()
        {
            var request = new SubscriptionService.Request(Users: 5, Months: 2, NonprofitClaimed: false, AutoRenew: false);
            _subscriptionStateMock.Setup(s => s.CurrentStatus()).Returns("Активна");

            var result = _service.Process(request);

            Assert.Equal("Продовжено", result.Status);
            Assert.Equal(200_000, result.Total); // 5 * 2 * 20_000
            Assert.Equal(0, result.RestorationFee);
            Assert.Equal("Активна", result.SubscriptionState);
        }

        [Fact] // Тест нарахування фіксованого штрафу для простроченої підписки
        public void Process_ExpiredSubscriptionWithRestorationFee_AddsFeeWithoutDiscount()
        {
            // Arrange
            var request = new SubscriptionService.Request(Users: 1, Months: 1, NonprofitClaimed: false, AutoRenew: false);
            _subscriptionStateMock.Setup(s => s.CurrentStatus()).Returns("Прострочена");

            // Act
            var result = _service.Process(request);

            // Assert
            Assert.Equal(25_000, result.Total); // 20_000 (база) + 5_000 (штраф)
            Assert.Equal(5_000, result.RestorationFee);
        }

        [Theory] // Параметризований тест розрахунку суми для різних комбінацій знижок
        [InlineData(true, true, true, 160_000)]   // НКО заявлено, статус підтверджено, є автопоновлення -> знижка 20%
        [InlineData(true, false, true, 190_000)]  // НКО заявлено, статус НЕ підтверджено, є автопоновлення -> знижка 5%
        [InlineData(false, false, true, 190_000)] // НКО не заявлено, є автопоновлення -> знижка 5%
        [InlineData(false, false, false, 200_000)]// Жодних знижок -> 0%
        public void Process_VariousDiscountCombinations_CalculatesExpectedTotal(
            bool claimed, bool eligible, bool autoRenew, long expectedTotal)
        {
            var request = new SubscriptionService.Request(Users: 10, Months: 1, NonprofitClaimed: claimed, AutoRenew: autoRenew);
            _subscriptionStateMock.Setup(s => s.CurrentStatus()).Returns("Активна");
            _nonprofitRegistryMock.Setup(n => n.IsEligible()).Returns(eligible);

            var result = _service.Process(request);

            Assert.Equal(expectedTotal, result.Total);
        }

        //----------------------------------------------------------------------------
        [Fact] // Тест використання стабу для статусу НКО та моку для перевірки репозиторію
        public void Process_UsesStubForNonprofitStatusAndMockForRepositoryVerification()
        {
            var request = new SubscriptionService.Request(Users: 1, Months: 1, NonprofitClaimed: true, AutoRenew: false);
            _subscriptionStateMock.Setup(s => s.CurrentStatus()).Returns("Активна");
            _nonprofitRegistryMock.Setup(n => n.IsEligible()).Returns(true); // Stub повертає заздалегідь визначений результат

            var result = _service.Process(request);

            // змінив 16 
            Assert.Equal(18_000, result.Total); // Базова вартість 20 000 зі знижкою 10% для НКО

            _billingRepositoryMock.Verify(
                b => b.Save(It.IsAny<SubscriptionService.Request>(), It.IsAny<SubscriptionService.Result>()),
                Times.Once,
                "Метод збереження в репозиторій повинен викликатися рівно один раз при успішному виконанні."
            );
        }

        [Fact] // Тест перевірки стану та суми для активної підписки
        public void Process_ActiveSubscription_ReturnsExpectedStateAndCalculatedTotal()
        {
            var request = new SubscriptionService.Request(Users: 4, Months: 3, NonprofitClaimed: false, AutoRenew: false);
            _subscriptionStateMock.Setup(s => s.CurrentStatus()).Returns("Активна");

            var result = _service.Process(request);

            Assert.Equal("Продовжено", result.Status);
            Assert.Equal(240_000, result.Total); // 4 користувачі * 3 місяці * 20 000
            Assert.Equal(0, result.RestorationFee);
            Assert.Equal("Активна", result.SubscriptionState);
        }

        [Fact] // Тест перевірки розрахунку штрафу та загальної суми для простроченої підписки
        public void Process_ExpiredSubscription_ReturnsCorrectRestorationFeeAndTotal()
        {
            var request = new SubscriptionService.Request(Users: 2, Months: 1, NonprofitClaimed: false, AutoRenew: false);
            _subscriptionStateMock.Setup(s => s.CurrentStatus()).Returns("Прострочена");

            var result = _service.Process(request);

            Assert.Equal("Продовжено", result.Status);
            Assert.Equal(45_000, result.Total); // (2 користувачі * 20 000) + 5 000 (штраф)
            Assert.Equal(5_000, result.RestorationFee);
        }

        [Fact] // Тест перевірки успішної взаємодії та передачі аргументів у репозиторій
        public void Process_SuccessfulOperation_VerifiesRepositorySaveInteractionAndArguments()
        {
            var request = new SubscriptionService.Request(Users: 2, Months: 3, NonprofitClaimed: false, AutoRenew: false);
            _subscriptionStateMock.Setup(s => s.CurrentStatus()).Returns("Активна");

            var result = _service.Process(request);

            _billingRepositoryMock.Verify(
                b => b.Save(
                    It.Is<SubscriptionService.Request>(r => r.Users == 2 && r.Months == 3),
                    It.Is<SubscriptionService.Result>(res => res.Total == 120_000)
                ),
                Times.Once,
                "Метод збереження в репозиторій має бути викликаний рівно один раз із правильними аргументами."
            );
        }

        [Fact] // Тест перевірки відсутності взаємодії з репозиторієм для заблокованої підписки
        public void Process_BlockedSubscription_VerifiesNoRepositoryInteraction()
        {
            var request = new SubscriptionService.Request(Users: 1, Months: 1, NonprofitClaimed: false, AutoRenew: false);
            _subscriptionStateMock.Setup(s => s.CurrentStatus()).Returns("Заблокована");

            var result = _service.Process(request);

            Assert.Equal("Підписку заблоковано", result.Status);
            _billingRepositoryMock.Verify(
                b => b.Save(It.IsAny<SubscriptionService.Request>(), It.IsAny<SubscriptionService.Result>()),
                Times.Never,
                "Для заблокованої підписки збереження в репозиторій виконуватися не повинно."
            );
        }

        [Fact] // Тест перевірки викидання винятку для від'ємної кількості місяців
        public void Process_InvalidMonthsCount_ThrowsArgumentException()
        {
            var request = new SubscriptionService.Request(Users: 5, Months: -1, NonprofitClaimed: false, AutoRenew: false);
            _subscriptionStateMock.Setup(s => s.CurrentStatus()).Returns("Активна");

            Assert.Throws<ArgumentException>(() => _service.Process(request));
        }

        [Fact] // Комплексний тест координації всіх залежностей у повному бізнес-сценарії
        public void Process_ComplexWorkflowWithAllDependencies_CoordinatesSuccessfully()
        {
            var request = new SubscriptionService.Request(Users: 5, Months: 3, NonprofitClaimed: true, AutoRenew: true);

            _subscriptionStateMock.Setup(s => s.CurrentStatus()).Returns("Активна");
            _nonprofitRegistryMock.Setup(n => n.IsEligible()).Returns(true);

            var result = _service.Process(request);

            Assert.Equal("Продовжено", result.Status);

            _subscriptionStateMock.Verify(s => s.CurrentStatus(), Times.Once, "Стан підписки має перевірятися рівно один раз.");
            _nonprofitRegistryMock.Verify(n => n.IsEligible(), Times.Once, "Реєстр НКО має перевірятися при наявності заявки.");
            _billingRepositoryMock.Verify(
                b => b.Save(It.IsAny<SubscriptionService.Request>(), It.IsAny<SubscriptionService.Result>()),
                Times.Once,
                "Результат успішної транзакції має бути збережений у репозиторій."
            );
        }

        #endregion

        #region 3.2 / 3.3 Coverage Gap Fixes (Покриття виявлених прогалин)

        [Fact]
        public void Constructor_NullSubscriptionState_ThrowsArgumentNullException()
        {
            // Закриває непокриту гілку null-перевірки в конструкторі (рядки 32-34)
            Assert.Throws<ArgumentNullException>(() =>
                new SubscriptionService(null, _nonprofitRegistryMock.Object, _billingRepositoryMock.Object));
        }

        [Fact]
        public void Constructor_NullNonprofitRegistry_ThrowsArgumentNullException()
        {
            // Закриває непокриту гілку перевірки другого аргументу конструктора
            Assert.Throws<ArgumentNullException>(() =>
                new SubscriptionService(_subscriptionStateMock.Object, null, _billingRepositoryMock.Object));
        }

        [Fact]
        public void Constructor_NullBillingRepository_ThrowsArgumentNullException()
        {
            // Закриває непокриту гілку перевірки третього аргументу конструктора
            Assert.Throws<ArgumentNullException>(() =>
                new SubscriptionService(_subscriptionStateMock.Object, _nonprofitRegistryMock.Object, null));
        }

        [Fact]
        public void Process_InvalidUsersCount_ThrowsArgumentException()
        {
            // Перевіряє викидання винятку для некоректної кількості користувачів (покриває валідаційні гілки у Process)
            var request = new SubscriptionService.Request(Users: 0, Months: 1, NonprofitClaimed: false, AutoRenew: false);
            Assert.Throws<ArgumentException>(() => _service.Process(request));
        }

        [Fact]
        public void Process_NullRequest_ThrowsArgumentException()
        {
            // Закриває непокритий рядок 39-40 (перевірка request на null)
            Assert.Throws<ArgumentException>(() => _service.Process(null));
        }

        [Fact]
        public void Process_UnknownSubscriptionState_ThrowsInvalidOperationException()
        {
            // Arrange
            // Налаштовуємо стаб так, щоб він повертав невідомий стан
            var stateMock = new Mock<SubscriptionService.ISubscriptionState>();
            stateMock.Setup(s => s.CurrentStatus()).Returns("НевідомийСтан");

            var serviceWithUnknownState = new SubscriptionService(
                stateMock.Object,
                _nonprofitRegistryMock.Object,
                _billingRepositoryMock.Object
            );

            var request = new SubscriptionService.Request(Users: 5, Months: 2, NonprofitClaimed: false, AutoRenew: false);

            // Закриває непокриті рядки 44-45 (викидання винятку для невідомого стану)
            Assert.Throws<InvalidOperationException>(() => serviceWithUnknownState.Process(request));
        }

        [Fact]
        public void Process_LongTermSubscription_AppliesLargeDurationDiscount()
        {
            _subscriptionStateMock.Setup(s => s.CurrentStatus()).Returns("Активна");

            var request = new SubscriptionService.Request(Users: 1, Months: 12, NonprofitClaimed: false, AutoRenew: false);

            var result = _service.Process(request);

            Assert.NotNull(result);
            Assert.Equal(230_000, result.Total);
        }

        [Fact]
        public void Process_MonthsGreaterOrEqual11_AppliesAdditionalDiscount()
        {
            // Arrange
            // Налаштовуємо стаб на активний стан
            _subscriptionStateMock.Setup(s => s.CurrentStatus()).Returns("Активна");

            // Встановлюємо Months = 12, щоб спрацювала умова request.Months >= 11 (рядок 61)
            var request = new SubscriptionService.Request(Users: 1, Months: 12, NonprofitClaimed: false, AutoRenew: false);

            var result = _service.Process(request);

            Assert.NotNull(result);
            // Очікувана сума: (1 * 12 * 20000) = 240000 - 10000 = 230000
            Assert.Equal(230_000, result.Total);
        }
        #endregion

    }
} 