using NativeDCB.Commerce.Commands;
using NativeDCB.Commerce.Events;
using NativeDCB.Commerce.Models;
using NativeDCB.Sdk.Decisions.Authoring;

namespace NativeDCB.Commerce;

public static class CommerceDecisionDefinitions
{
    public static DecisionDefinition<PublishProduct> PublishProductSdk()
    {
        PublishProduct template = new("", "", 0, "");
        return Decision.WithDecisionModel(template)
            .Include<ProductPublished, ProductPublicationSdkModel>((_, _) => new ProductPublicationSdkModel
            {
                ProductExists = true
            })
            .Where(@event => @event.Sku == template.Sku)
            .Evaluate((model, command) =>
                command.Sku != "" &&
                command.Name != "" &&
                command.UnitPriceMinor >= 0 &&
                command.Currency != "" &&
                !(model.ProductExists ?? false))
            .Decide((accepted, command) => accepted
                ? Decision.Accept(new ProductPublished(
                    command.Sku,
                    command.Name,
                    command.UnitPriceMinor,
                    command.Currency))
                : Decision.Reject("PublishProduct validation failed"));
    }

    public static DecisionDefinition<ReserveCartLine> ReserveCartLineSdk()
    {
        ReserveCartLine template = new("", "", "", "", "", 0);
        return Decision.WithDecisionModel(template)
            .Include<CartOpened, ReserveCartLineSdkModel>((_, @event) => new ReserveCartLineSdkModel
            {
                CartExists = true,
                CartCustomerId = @event.CustomerId,
                CartCurrency = @event.Currency
            })
            .Where(@event => @event.CartId == template.CartId)
            .Include<CartCheckedOut>((_, _) => new ReserveCartLineSdkModel
            {
                CartCheckedOut = true
            })
            .Where(@event => @event.CartId == template.CartId)
            .Include<ProductPublished>((_, @event) => new ReserveCartLineSdkModel
            {
                ProductExists = true,
                ProductActive = true,
                UnitPriceMinor = @event.UnitPriceMinor,
                ProductCurrency = @event.Currency
            })
            .Where(@event => @event.Sku == template.Sku)
            .Include<ProductPriceChanged>((_, @event) => new ReserveCartLineSdkModel
            {
                UnitPriceMinor = @event.UnitPriceMinor,
                ProductCurrency = @event.Currency
            })
            .Where(@event => @event.Sku == template.Sku)
            .Include<ProductDiscontinued>((_, _) => new ReserveCartLineSdkModel
            {
                ProductActive = false
            })
            .Where(@event => @event.Sku == template.Sku)
            .Include<InventoryReceived>((model, @event) => new ReserveCartLineSdkModel
            {
                AvailableQuantity = (model.AvailableQuantity ?? 0) + @event.Quantity
            })
            .Where(@event =>
                @event.Sku == template.Sku && @event.WarehouseId == template.WarehouseId)
            .Include<InventoryAdjusted>((model, @event) => new ReserveCartLineSdkModel
            {
                AvailableQuantity = (model.AvailableQuantity ?? 0) + @event.QuantityDelta
            })
            .Where(@event =>
                @event.Sku == template.Sku && @event.WarehouseId == template.WarehouseId)
            .Include<InventoryReserved>((model, @event) => new ReserveCartLineSdkModel
            {
                AvailableQuantity = (model.AvailableQuantity ?? 0) - @event.Quantity
            })
            .Where(@event =>
                @event.Sku == template.Sku && @event.WarehouseId == template.WarehouseId)
            .Include<InventoryReleased>((model, @event) => new ReserveCartLineSdkModel
            {
                AvailableQuantity = (model.AvailableQuantity ?? 0) + @event.Quantity
            })
            .Where(@event =>
                @event.Sku == template.Sku && @event.WarehouseId == template.WarehouseId)
            .Include<ReturnedInventoryRestocked>((model, @event) => new ReserveCartLineSdkModel
            {
                AvailableQuantity = (model.AvailableQuantity ?? 0) + @event.Quantity
            })
            .Where(@event =>
                @event.Sku == template.Sku && @event.WarehouseId == template.WarehouseId)
            .Include<CartLineReserved>((_, _) => new ReserveCartLineSdkModel
            {
                LineUsed = true
            })
            .Where(@event => @event.CartId == template.CartId && @event.LineId == template.LineId)
            .Include<CartLineRemoved>((_, _) => new ReserveCartLineSdkModel
            {
                LineUsed = true
            })
            .Where(@event => @event.CartId == template.CartId && @event.LineId == template.LineId)
            .Evaluate((model, command) => new ReserveCartLineSdkEvaluation(
                command.CartId != "" &&
                command.LineId != "" &&
                command.CustomerId != "" &&
                command.Sku != "" &&
                command.WarehouseId != "" &&
                command.Quantity > 0 &&
                (model.CartExists ?? false) &&
                model.CartCustomerId == command.CustomerId &&
                !(model.CartCheckedOut ?? false) &&
                (model.ProductExists ?? false) &&
                (model.ProductActive ?? false) &&
                model.ProductCurrency == model.CartCurrency &&
                !(model.LineUsed ?? false) &&
                (model.AvailableQuantity ?? 0) >= command.Quantity,
                model.UnitPriceMinor ?? 0,
                (model.UnitPriceMinor ?? 0) * command.Quantity,
                model.CartCurrency ?? ""))
            .Decide((evaluation, command) => evaluation.Accepted
                ? Decision.Accept(
                    new CartLineReserved(
                        command.CartId,
                        command.LineId,
                        command.CustomerId,
                        command.Sku,
                        command.WarehouseId,
                        command.Quantity,
                        evaluation.UnitPriceMinor,
                        evaluation.LineTotalMinor,
                        evaluation.Currency),
                    new InventoryReserved(
                        command.Sku,
                        command.WarehouseId,
                        command.CartId,
                        command.LineId,
                        command.Quantity))
                : Decision.Reject("ReserveCartLine validation failed"));
    }

    public static DecisionDefinition<CheckoutCart> CheckoutCartSdk()
    {
        CheckoutCart template = new("", "", "");
        return Decision.WithDecisionModel(template)
            .Include<CartOpened, CheckoutCartSdkModel>((_, @event) => new CheckoutCartSdkModel
            {
                CartExists = true,
                CartCustomerId = @event.CustomerId,
                CartCurrency = @event.Currency
            })
            .Where(@event => @event.CartId == template.CartId)
            .Include<CartLineReserved>((model, @event) => new CheckoutCartSdkModel
            {
                ActiveLineCount = (model.ActiveLineCount ?? 0) + 1,
                GrossTotalMinor = (model.GrossTotalMinor ?? 0) + @event.LineTotalMinor
            })
            .Where(@event => @event.CartId == template.CartId)
            .Include<CartLineRemoved>((model, @event) => new CheckoutCartSdkModel
            {
                ActiveLineCount = (model.ActiveLineCount ?? 0) - 1,
                GrossTotalMinor = (model.GrossTotalMinor ?? 0) - @event.LineTotalMinor
            })
            .Where(@event => @event.CartId == template.CartId)
            .Include<CartCheckedOut>((_, _) => new CheckoutCartSdkModel
            {
                CartCheckedOut = true
            })
            .Where(@event => @event.CartId == template.CartId)
            .Include<OrderPlaced>((_, _) => new CheckoutCartSdkModel
            {
                OrderExists = true
            })
            .Where(@event => @event.OrderId == template.OrderId)
            .Evaluate((model, command) => new CheckoutCartSdkEvaluation(
                command.CartId != "" &&
                command.OrderId != "" &&
                command.CustomerId != "" &&
                (model.CartExists ?? false) &&
                model.CartCustomerId == command.CustomerId &&
                !(model.CartCheckedOut ?? false) &&
                (model.ActiveLineCount ?? 0) > 0 &&
                !(model.OrderExists ?? false),
                model.ActiveLineCount ?? 0,
                model.GrossTotalMinor ?? 0,
                model.CartCurrency ?? ""))
            .Decide((evaluation, command) => evaluation.Accepted
                ? Decision.Accept(
                    new CartCheckedOut(
                        command.CartId,
                        command.OrderId,
                        command.CustomerId,
                        evaluation.LineCount,
                        evaluation.GrossTotalMinor,
                        evaluation.Currency),
                    new OrderPlaced(
                        command.OrderId,
                        command.CartId,
                        command.CustomerId,
                        evaluation.LineCount,
                        evaluation.GrossTotalMinor,
                        evaluation.Currency))
                : Decision.Reject("CheckoutCart validation failed"));
    }
}
