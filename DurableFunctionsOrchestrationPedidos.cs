using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;

namespace MyEcommerce.Pedidos
{
    public static class ProductData
    {
        public static readonly Dictionary<string, decimal> Products = new Dictionary<string, decimal>
        {
            {"Product1", 10.00m},
            {"Product2", 15.50m},
            {"Product3", 7.99m},
            {"Product4", 12.30m},
            {"Product5", 5.00m}
        };
    }

    public class OrderInfo
    {
        public List<CartItem> CartItems { get; set; }
        public OrderDetails OrderDetails { get; set; }
    }

    public class CartItem
    {
        public string ProductName { get; set; }
        public int Quantity { get; set; }
    }

    public class OrderDetails
    {
        public string Address { get; set; }
        public bool IsConfirmed { get; set; }
        public string OrderId { get; set; }
    }

    public class OrderCompletionInfo
    {
        public string OrderId { get; set; }
        public bool DeliverySuccessful { get; set; }
    }

    public static class DurableFunctionsOrchestration
    {
        [FunctionName("DurableFunctionsOrchestration")]
        public static async Task RunOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context)
        {
            var orderInfo = context.GetInput<OrderInfo>();

            // Gerar OrderId
            string orderId = Guid.NewGuid().ToString();
            orderInfo.OrderDetails.OrderId = orderId;

            await context.CallActivityAsync("AddToCartFunction", orderInfo.CartItems);
            await context.CallActivityAsync("ConfirmOrderFunction", orderInfo.OrderDetails);
            await context.CallActivityAsync("CompleteOrderFunction", new OrderCompletionInfo { OrderId = orderId, DeliverySuccessful = true });
        }

        [FunctionName("ListProducts")]
        public static IActionResult ListProducts(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequest req)
        {
            string responseMessage = JsonConvert.SerializeObject(ProductData.Products);
            return new OkObjectResult(responseMessage);
        }
    }

    public static class AddToCartFunction
    {
        private static Dictionary<string, int> Cart = new Dictionary<string, int>();
        private static decimal TotalOrder = 0m;

        public static Dictionary<string, int> GetCartItems()
        {
            return Cart;
        }

        [FunctionName("AddToCart")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var cartItems = JsonConvert.DeserializeObject<List<CartItem>>(requestBody);

            foreach (var cartItem in cartItems)
            {
                if (!ProductData.Products.ContainsKey(cartItem.ProductName))
                {
                    return new BadRequestObjectResult("Produto não encontrado: " + cartItem.ProductName);
                }

                decimal price = ProductData.Products[cartItem.ProductName];
                decimal itemTotal = price * cartItem.Quantity;

                if (Cart.ContainsKey(cartItem.ProductName))
                {
                    Cart[cartItem.ProductName] += cartItem.Quantity;
                }
                else
                {
                    Cart.Add(cartItem.ProductName, cartItem.Quantity);
                }

                TotalOrder += itemTotal;

                log.LogInformation($"Item added to cart: {cartItem.ProductName}, Quantity: {cartItem.Quantity}, Total do Item: {itemTotal}");
            }

            var response = new
            {
                Cart = Cart,
                TotalOrder = TotalOrder
            };

            return new OkObjectResult(response);
        }
    }

    public static class ConfirmOrderFunction
    {
        public static HashSet<string> confirmedOrderIds = new HashSet<string>();

        [FunctionName("ConfirmOrder")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var orderDetails = JsonConvert.DeserializeObject<OrderDetails>(requestBody);

            if (!orderDetails.IsConfirmed)
            {
                return new BadRequestObjectResult("Pedido não confirmado.");
            }

            // Gerar OrderId
            orderDetails.OrderId = Guid.NewGuid().ToString();
            confirmedOrderIds.Add(orderDetails.OrderId);

            decimal totalOrder = 0m;
            var cartItems = AddToCartFunction.GetCartItems();
            foreach (var item in cartItems)
            {
                totalOrder += ProductData.Products[item.Key] * item.Value;
            }

            log.LogInformation($"Pedido confirmado: {orderDetails.OrderId}, Endereço: {orderDetails.Address}, Total do Pedido: {totalOrder}");
            return new OkObjectResult(new { OrderId = orderDetails.OrderId, Address = orderDetails.Address, TotalOrder = totalOrder, Status = "Confirmado" });
        }
    }

    public static class UpdateOrderStatusFunction
    {
        [FunctionName("UpdateOrderStatus")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            log.LogInformation($"Order update received: {requestBody}");
            return new OkObjectResult($"Received order update: {requestBody}");
        }
    }

    public static class CompleteOrderFunction
    {
        [FunctionName("CompleteOrder")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            var completionInfo = JsonConvert.DeserializeObject<OrderCompletionInfo>(requestBody);

            if (!ConfirmOrderFunction.confirmedOrderIds.Contains(completionInfo.OrderId))
            {
                log.LogInformation($"OrderId inválido ou não confirmado: {completionInfo.OrderId}");
                return new BadRequestObjectResult($"OrderId inválido ou não confirmado: {completionInfo.OrderId}");
            }

            if (completionInfo.DeliverySuccessful)
            {
                log.LogInformation($"Entrega do pedido {completionInfo.OrderId} concluída com sucesso.");
                return new OkObjectResult($"Pedido {completionInfo.OrderId} finalizado com sucesso.");
            }
            else
            {
                log.LogInformation($"Problemas na entrega do pedido {completionInfo.OrderId}.");
                return new BadRequestObjectResult($"Falha ao finalizar o pedido {completionInfo.OrderId}.");
            }
        }
    }
}