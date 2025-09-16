using Microsoft.EntityFrameworkCore;
using ProjectE.Data;
using ProjectE.Models.Entities;
using ProjectE.Models.Enums;
using ProjectE.Models.MenuModels;
using ProjectE.Models.OrderModels;
using ProjectE.Services;
using ProjectE.Exceptions;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace ProjectE.UnitTests
{
    public class OrderServiceTests
    {
        #region Helpers
        private async Task<ApplicationDbContext> GetInMemoryDbContext()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;

            var context = new ApplicationDbContext(options);

            // Seed data
            context.Users.Add(new UserEntity
            {
                Id = 1,
                UserId = "user-1",
                UserName = "لينا أبوفرحة",
                Password = "123",
                Role = "Customer",
                Email = "lina@test.com",
                PhoneNumber = "0598765432"
            });

            context.MenuItems.Add(new MenuItemEntity
            {
                Id = 1,
                Name = "Pizza",
                Price = 10,
                Category = "Food",
                IsAvailable = true,
            });

            context.MenuItems.Add(new MenuItemEntity
            {
                Id = 2,
                Name = "Tea",
                Price = 5,
                Category = "Drinks",
                IsAvailable = true
            });

            await context.SaveChangesAsync();

            return context;
        }

        private ChatOrderRequest BuildValidOrderRequest()
        {
            return new ChatOrderRequest
            {
                CustomerName = "لينا أبوفرحة",
                PhoneNumber = "0598765432",
                DeliveryAddress = "شارع الجامعة، رام الله",
                Notes = "",
                Items = new List<OrderItemRequest>
                {
                    new OrderItemRequest { MenuItemId = 1, Name = "Pizza", Quantity = 2,Notes = "No cheese" },
                    new OrderItemRequest { MenuItemId = 2, Name = "Tea", Quantity = 1,Notes = "Less sugar" }
                }
            };
        }

        private async Task<OrderEntity> CreateTestOrder(ApplicationDbContext dbContext, int userId, OrderStatus status)
        {
            var order = new OrderEntity
            {
                UserId = userId,
                CreatedAt = DateTime.UtcNow,
                CustomerName = "لينا أبوفرحة",
                PhoneNumber = "0598765432",
                DeliveryAddress = "شارع الجامعة، رام الله",
                Status = status.ToString(),
                Notes = "",
                Items = new List<OrderItemEntity>
        {
            new OrderItemEntity { MenuItemId = 1, Quantity = 2 ,Notes = "No cheese"},
            new OrderItemEntity { MenuItemId = 2, Quantity = 1 ,Notes = "Less sugar"}
        }
            };

            dbContext.Orders.Add(order);
            await dbContext.SaveChangesAsync();

            return order;
        }


        #endregion

        #region Create Tests
        [Fact]
        public async Task CreateOrder_ShouldReturnOrderResponse_WhenDataIsValid()
        {
            // Arrange
            var context = await GetInMemoryDbContext();
            var service = new OrderService(context);
            var request = BuildValidOrderRequest();
            var userId = "user-1";

            // Act
            var result = await service.CreateOrder(userId, request);

            // Assert - response checks
            Assert.NotNull(result);
            Assert.Equal(request.CustomerName, result.CustomerName);
            Assert.Equal(2, result.Items.Count);
            Assert.Equal(OrderStatus.Pending, result.Status);

            // Assert - verify DB persisted data
            var createdOrder = await context.Orders
                .Include(o => o.Items)
                .FirstOrDefaultAsync(o => o.Id == result.OrderId);

            Assert.NotNull(createdOrder);
            Assert.Equal(1, createdOrder.UserId); // internal user id
            Assert.Equal(2, createdOrder.Items.Count);
            Assert.Equal(request.Items[0].Notes, result.Items[0].Notes);
            Assert.Equal(request.Items[1].Notes, result.Items[1].Notes);

            //// Assert - verify total price logic
            // 1. Calculate expected subtotal
            var expectedSubtotal = request.Items.Sum(i =>
                i.Quantity * context.MenuItems.First(m => m.Id == i.MenuItemId).Price);
            Assert.Equal(expectedSubtotal, result.Subtotal);

            // 2. Calculate expected delivery fee
            var expectedDeliveryFee = service.GetDeliveryFee(request.DeliveryAddress);
            Assert.Equal(expectedDeliveryFee, result.DeliveryFee);

            // 3. Verify the final total price
            var expectedTotalPrice = expectedSubtotal + expectedDeliveryFee;
            Assert.Equal(expectedTotalPrice, result.TotalPrice);

        }

        [Fact]
        public async Task CreateOrder_ShouldThrowMenuItemNotFound_WhenMenuItemDoesNotExist()
        {
            // Arrange
            var context = await GetInMemoryDbContext();
            var service = new OrderService(context);
            var request = BuildValidOrderRequest();
            request.Items[0].MenuItemId = 999;
            request.Items[0].Quantity = 1;

            var userId = "user-1";

            // Act & Assert
            var ex = await Assert.ThrowsAsync<MenuItemNotFoundException>(() => service.CreateOrder(userId, request));
            Assert.Contains("not found", ex.Message);
        }

        [Fact]
        public async Task CreateOrder_ShouldThrowException_WhenMenuItemNotAvailable()
        {
            // Arrange
            var context = await GetInMemoryDbContext();
            // Make the MenuItem unavailable
            var menuItem = await context.MenuItems.FirstAsync();
            menuItem.IsAvailable = false;
            await context.SaveChangesAsync();

            var service = new OrderService(context);
            var request = BuildValidOrderRequest();
            var userId = "user-1";

            // Act & Assert
            var ex = await Assert.ThrowsAsync<MenuItemUnavailableException>(() => service.CreateOrder(userId, request));
            Assert.Contains("not available", ex.Message);
        }

        [Fact]
        public async Task CreateOrder_ShouldThrowException_WhenUserIdNotFound()
        {
            // Arrange
            var context = await GetInMemoryDbContext();
            var service = new OrderService(context);
            var request = BuildValidOrderRequest();
            var userId = "non-existent-user";

            // Act & Assert
            var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateOrder(userId, request));
            Assert.Contains("User not found", ex.Message);
        }
        [Fact]
        public async Task CreateOrder_ShouldThrowException_WhenQuantityIsZeroOrNegative()
        {
            // Arrange
            var context = await GetInMemoryDbContext();
            var service = new OrderService(context);
            var request = BuildValidOrderRequest();
            request.Items[0].Quantity = 0;
            request.Items[1].Quantity = -2;

            var userId = "user-1";

            // Act & Assert
            var ex = await Assert.ThrowsAsync<InvalidQuantityException>(() => service.CreateOrder(userId, request));
            Assert.Contains("Quantity must be greater than zero", ex.Message);
        }
        [Fact]
        public async Task CreateOrder_ShouldThrowException_WhenDuplicateMenuItemsExist()
        {
            // Arrange
            var context = await GetInMemoryDbContext();
            var service = new OrderService(context);
            var request = BuildValidOrderRequest();
            request.Items[0].MenuItemId = 1;
            request.Items[1].MenuItemId = 1;

            string userId = "user-1";

            // Act & Assert
            var ex = await Assert.ThrowsAsync<DuplicateMenuItemException>(() => service.CreateOrder(userId, request));
            Assert.Contains("Duplicate", ex.Message);
        }
        [Fact]
        public async Task CreateOrder_ShouldThrowException_WhenDatabaseFails()
        {
            // Arrange
            var context = await GetInMemoryDbContext();
            context.Dispose();
            var service = new OrderService(context);
            var request = BuildValidOrderRequest();
            var userId = "user-1";
            // Act & Assert
            await Assert.ThrowsAsync<ObjectDisposedException>(() => service.CreateOrder(userId, request));
        }

        [Fact]
        public async Task CreateOrder_ShouldThrowException_WhenItemListIsEmpty()
        {
            // Arrange
            var context = await GetInMemoryDbContext();
            var service = new OrderService(context);

            var request = new ChatOrderRequest
            {
                CustomerName = "لينا أبوفرحة",
                PhoneNumber = "0598765432",
                DeliveryAddress = "شارع الجامعة، رام الله",
                Notes = "",
                Items = new List<OrderItemRequest>() 
            };

            var userId = "user-1";

            // Act & Assert
            var ex = await Assert.ThrowsAsync<EmptyOrderItemsException>(() => service.CreateOrder(userId, request));
            Assert.Contains("at least one item", ex.Message);
        }

        [Fact]
        public async Task CreateOrder_ShouldUseUserInfo_WhenNameAndPhoneNotProvided()
        {
            // Arrange
            var context = await GetInMemoryDbContext();

            // We save the user name and number from the data for comparison..
            var user = await context.Users.FirstAsync();
            var service = new OrderService(context);

            var request = new ChatOrderRequest
            {
                CustomerName = null,
                PhoneNumber = null, 
                DeliveryAddress = "شارع الجامعة، رام الله",
                Notes = " ",
                Items = new List<OrderItemRequest>
                {
                    new OrderItemRequest { MenuItemId = 1, Quantity = 1 }
                }
            };

            string userId = user.UserId;

            // Act
            var createdOrder = await service.CreateOrder(userId, request);

            // Assert
            Assert.NotNull(createdOrder);
            Assert.Equal(user.UserName, createdOrder.CustomerName); // It must be taken from the stored data
            Assert.Equal(user.PhoneNumber, createdOrder.PhoneNumber);
        }


        #endregion

        #region GetOrdersByUserId Tests

        [Fact]
        public async Task GetOrdersByUserId_ShouldReturnOrders_WhenUserHasOrders()
        {
            // Arrange
            var context = await GetInMemoryDbContext();
            var service = new OrderService(context);

            // Add an order for the existing user 
            var order = await CreateTestOrder(context, 1, OrderStatus.Pending);
            string userId = "user-1"; // external user id

            // Act
            var result = await service.GetOrdersByUserId(userId);

            // Assert
            Assert.NotNull(result);
            var ordersList = result.ToList();
            Assert.Single(ordersList);
            Assert.Equal(order.CustomerName, ordersList[0].CustomerName);
            Assert.Equal(order.DeliveryAddress, ordersList[0].DeliveryAddress);
            Assert.Equal(2, ordersList[0].Items.Count);
            Assert.Equal(OrderStatus.Pending, ordersList[0].Status);

        }

        [Fact]
        public async Task GetOrdersByUserId_ShouldReturnEmpty_WhenUserHasNoOrders()
        {
            // Arrange
            var context = await GetInMemoryDbContext();
            var service = new OrderService(context);
            string userId = "user-1"; // User is present without orders

            // Act
            var result = await service.GetOrdersByUserId(userId);

            // Assert
            Assert.NotNull(result);
            Assert.Empty(result);
        }

        [Fact]
        public async Task GetOrdersByUserId_ShouldThrowException_WhenUserNotFound()
        {
            // Arrange
            var context = await GetInMemoryDbContext();
            var service = new OrderService(context);
            string userId = "invalid-user";

            // Act & Assert
            var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetOrdersByUserId(userId));
            Assert.Contains("User not found", ex.Message);
        }

        [Fact]
        public async Task GetOrdersByUserId_ShouldThrowException_WhenDatabaseDisposed()
        {
            // Arrange
            var context = await GetInMemoryDbContext();
            context.Dispose(); // Simulate database failure.

            var service = new OrderService(context);

            // Act & Assert
            await Assert.ThrowsAsync<ObjectDisposedException>(() => service.GetOrdersByUserId("user-1"));
        }
        #endregion

        #region CancelOrderByUser Tests

        [Fact]
        public async Task CancelOrderByUser_ShouldThrowUnauthorized_WhenUserNotFound()
        {
            // Arrange
            var dbContext = await GetInMemoryDbContext();
            var service = new OrderService(dbContext);
            var userId = "invalid-user";

            // Act & Assert
            var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CancelOrderByUser(userId, 1)); 
            Assert.Contains("User not found", ex.Message);
        }

        [Fact]
        public async Task CancelOrderByUser_ShouldThrowKeyNotFound_WhenOrderNotFound()
        {
            // Arrange
            var dbContext = await GetInMemoryDbContext();
            var service = new OrderService(dbContext);
            var userId = "user-1"; //

            // Act + Assert
            var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() => service.CancelOrderByUser(userId, 999)); // OrderId does not exist
            Assert.Contains("Order not found", ex.Message); // OrderId does not exist
        }

        [Fact]
        public async Task CancelOrderByUser_ShouldThrowInvalidOperation_WhenStatusNotPending()
        {
            // Arrange
            var dbContext = await GetInMemoryDbContext();
            var service = new OrderService(dbContext);
            var userId = "user-1";

            var order = await CreateTestOrder(dbContext, 1, OrderStatus.OutForDelivery);

            // Act + Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CancelOrderByUser(userId, order.Id)); 
            Assert.Contains("You cannot cancel the order once it is in Processing or later.", ex.Message); 
        }

        [Fact]
        public async Task CancelOrderByUser_ShouldDeleteOrder_WhenStatusPending()
        {
            // Arrange
            var dbContext = await GetInMemoryDbContext();
            var service = new OrderService(dbContext);
            var userId = "user-1";

            var order = await CreateTestOrder(dbContext, 1, OrderStatus.Pending);

            // Act
            var result = await service.CancelOrderByUser(userId, order.Id);

            // Assert
            Assert.True(result);
            Assert.False(dbContext.Orders.Any(o => o.Id == order.Id));
            Assert.False(dbContext.OrderItems.Any(i => i.OrderId == order.Id));
        }

        #endregion
    }
}