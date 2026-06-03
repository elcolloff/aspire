// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.Hosting.Dashboard;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Aspire.Hosting.Dashboard.DashboardServiceData;

namespace Aspire.Hosting.Tests;

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

[Trait("Partition", "2")]
public class InteractionServiceTests
{
    [Fact]
    public async Task PromptConfirmationAsync_CompleteResult_ReturnResult()
    {
        // Arrange
        var interactionService = CreateInteractionService();

        // Act 1
        var resultTask = interactionService.PromptConfirmationAsync("Are you sure?", "Confirmation");

        // Assert 1
        var interaction = Assert.Single(interactionService.GetCurrentInteractions());
        Assert.False(interaction.CompletionTcs.Task.IsCompleted);
        Assert.Equal(Interaction.InteractionState.InProgress, interaction.State);

        // Act 2
        await CompleteInteractionAsync(interactionService, interaction.InteractionId, new InteractionCompletionState { Complete = true, State = true });

        var result = await resultTask.DefaultTimeout();
        Assert.True(result.Data);

        // Assert 2
        Assert.Empty(interactionService.GetCurrentInteractions());
    }

    [Fact]
    public async Task PromptConfirmationAsync_Cancellation_ReturnResult()
    {
        // Arrange
        var interactionService = CreateInteractionService();

        // Act 1
        var cts = new CancellationTokenSource();
        var resultTask = interactionService.PromptConfirmationAsync("Are you sure?", "Confirmation", cancellationToken: cts.Token);

        // Assert 1
        var interaction = Assert.Single(interactionService.GetCurrentInteractions());
        Assert.False(interaction.CompletionTcs.Task.IsCompleted);
        Assert.Equal(Interaction.InteractionState.InProgress, interaction.State);

        // Act 2
        cts.Cancel();

        var result = await resultTask.DefaultTimeout();
        Assert.True(result.Canceled);

        // Assert 2
        Assert.Empty(interactionService.GetCurrentInteractions());
    }

    [Fact]
    public async Task PromptConfirmationAsync_MultipleCompleteResult_ReturnResult()
    {
        // Arrange
        var interactionService = CreateInteractionService();

        // Act 1
        var resultTask1 = interactionService.PromptConfirmationAsync("Are you sure?", "Confirmation");
        var resultTask2 = interactionService.PromptConfirmationAsync("Are you sure?", "Confirmation");
        var resultTask3 = interactionService.PromptConfirmationAsync("Are you sure?", "Confirmation");

        // Assert 1
        int? id1 = null;
        int? id2 = null;
        int? id3 = null;
        Assert.Collection(interactionService.GetCurrentInteractions(),
            interaction =>
            {
                id1 = interaction.InteractionId;
            },
            interaction =>
            {
                id2 = interaction.InteractionId;
            },
            interaction =>
            {
                id3 = interaction.InteractionId;
            });
        Assert.True(id1.HasValue && id2.HasValue && id3.HasValue && id1 < id2 && id2 < id3);

        // Act & Assert 2
        await CompleteInteractionAsync(interactionService, id1.Value, new InteractionCompletionState { Complete = true, State = true });
        var result1 = await resultTask1.DefaultTimeout();
        Assert.True(result1.Data);
        Assert.False(result1.Canceled);
        Assert.Collection(interactionService.GetCurrentInteractions(),
            interaction => Assert.Equal(interaction.InteractionId, id2),
            interaction => Assert.Equal(interaction.InteractionId, id3));

        await CompleteInteractionAsync(interactionService, id2.Value, new InteractionCompletionState { Complete = true, State = false });
        var result2 = await resultTask2.DefaultTimeout();
        Assert.False(result2.Data);
        Assert.False(result1.Canceled);
        Assert.Equal(id3.Value, Assert.Single(interactionService.GetCurrentInteractions()).InteractionId);

        await CompleteInteractionAsync(interactionService, id3.Value, new InteractionCompletionState { Complete = true });
        var result3 = await resultTask3.DefaultTimeout();
        Assert.True(result3.Canceled);
        Assert.Empty(interactionService.GetCurrentInteractions());
    }

    [Fact]
    public async Task SubscribeInteractionUpdates_MultipleCompleteResult_ReturnResult()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        var subscription = interactionService.SubscribeInteractionUpdates();
        var updates = Channel.CreateUnbounded<Interaction>();
        var readTask = Task.Run(async () =>
        {
            await foreach (var interaction in subscription.WithCancellation(CancellationToken.None))
            {
                await updates.Writer.WriteAsync(interaction);
            }
        });

        // Act 1
        var resultTask1 = interactionService.PromptConfirmationAsync("Are you sure?", "Confirmation");
        var interaction1 = Assert.Single(interactionService.GetCurrentInteractions());
        Assert.Equal(interaction1.InteractionId, (await updates.Reader.ReadAsync().DefaultTimeout()).InteractionId);

        var resultTask2 = interactionService.PromptConfirmationAsync("Are you sure?", "Confirmation");
        Assert.Equal(2, interactionService.GetCurrentInteractions().Count);
        var interaction2 = interactionService.GetCurrentInteractions()[1];
        Assert.Equal(interaction2.InteractionId, (await updates.Reader.ReadAsync().DefaultTimeout()).InteractionId);

        // Act & Assert 2
        var result1 = new InteractionCompletionState { Complete = true, State = true };
        await CompleteInteractionAsync(interactionService, interaction1.InteractionId, result1);
        Assert.True((await resultTask1.DefaultTimeout()).Data);
        Assert.Equal(interaction2.InteractionId, Assert.Single(interactionService.GetCurrentInteractions()).InteractionId);
        var completedInteraction1 = await updates.Reader.ReadAsync().DefaultTimeout();
        Assert.True(completedInteraction1.CompletionTcs.Task.IsCompletedSuccessfully);
        Assert.Equivalent(result1, await completedInteraction1.CompletionTcs.Task.DefaultTimeout());

        var result2 = new InteractionCompletionState { Complete = true, State = false };
        await CompleteInteractionAsync(interactionService, interaction2.InteractionId, result2);
        Assert.False((await resultTask2.DefaultTimeout()).Data);
        Assert.Empty(interactionService.GetCurrentInteractions());
        var completedInteraction2 = await updates.Reader.ReadAsync().DefaultTimeout();
        Assert.True(completedInteraction2.CompletionTcs.Task.IsCompletedSuccessfully);
        Assert.Equivalent(result2, await completedInteraction2.CompletionTcs.Task.DefaultTimeout());
    }

    [Fact]
    public async Task PublicApis_DashboardDisabled_ThrowErrors()
    {
        // Arrange
        var interactionService = CreateInteractionService(options: new DistributedApplicationOptions { DisableDashboard = true });

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => interactionService.PromptConfirmationAsync("Are you sure?", "Confirmation")).DefaultTimeout();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => interactionService.PromptNotificationAsync("Are you sure?", "Confirmation")).DefaultTimeout();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => interactionService.PromptMessageBoxAsync("Are you sure?", "Confirmation")).DefaultTimeout();
    }

    [Fact]
    public void IsAvailable_DashboardEnabled_ReturnsTrue()
    {
        // Arrange & Act
        var interactionService = CreateInteractionService();

        // Assert
        Assert.True(interactionService.IsAvailable);
    }

    [Fact]
    public void IsAvailable_DashboardDisabled_ReturnsFalse()
    {
        // Arrange & Act
        var interactionService = CreateInteractionService(options: new DistributedApplicationOptions { DisableDashboard = true });

        // Assert
        Assert.False(interactionService.IsAvailable);
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("FALSE", false)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("TRUE", true)]
    public void IsAvailable_InteractivityEnabledConfigured_ReturnsExpectedValue(string configValue, bool expected)
    {
        // Arrange
        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPIRE_INTERACTIVITY_ENABLED"] = configValue
        });
        var configuration = configBuilder.Build();

        // Act
        var interactionService = new InteractionService(
            NullLogger<InteractionService>.Instance,
            new DistributedApplicationOptions(),
            new ServiceCollection().BuildServiceProvider(),
            configuration);

        // Assert
        Assert.Equal(expected, interactionService.IsAvailable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("invalid")]
    [InlineData("1")]
    [InlineData("0")]
    public void IsAvailable_InteractivityEnabledInvalidValue_ReturnsTrue(string configValue)
    {
        // Arrange
        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPIRE_INTERACTIVITY_ENABLED"] = configValue
        });
        var configuration = configBuilder.Build();

        // Act
        var interactionService = new InteractionService(
            NullLogger<InteractionService>.Instance,
            new DistributedApplicationOptions(),
            new ServiceCollection().BuildServiceProvider(),
            configuration);

        // Assert - Invalid values should be ignored, defaulting to true (since dashboard is enabled)
        Assert.True(interactionService.IsAvailable);
    }

    [Fact]
    public void IsAvailable_InteractivityDisabledAndDashboardDisabled_ReturnsFalse()
    {
        // Arrange
        var configBuilder = new ConfigurationBuilder();
        configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPIRE_INTERACTIVITY_ENABLED"] = "false"
        });
        var configuration = configBuilder.Build();

        // Act
        var interactionService = new InteractionService(
            NullLogger<InteractionService>.Instance,
            new DistributedApplicationOptions { DisableDashboard = true },
            new ServiceCollection().BuildServiceProvider(),
            configuration);

        // Assert - Both conditions should result in false
        Assert.False(interactionService.IsAvailable);
    }

    [Fact]
    public void IsAvailable_NonInteractiveScope_ReturnsFalse()
    {
        var interactionService = CreateInteractionService();

        Assert.True(interactionService.IsAvailable);

        using (InteractionService.StartNonInteractiveScope())
        {
            Assert.False(interactionService.IsAvailable);
        }

        Assert.True(interactionService.IsAvailable);
    }

    [Fact]
    public async Task IsAvailable_NonInteractiveScope_FlowsAcrossAsyncCalls()
    {
        var interactionService = CreateInteractionService();

        Assert.True(interactionService.IsAvailable);

        using (InteractionService.StartNonInteractiveScope())
        {
            Assert.False(interactionService.IsAvailable);

            await Task.Yield();

            // AsyncLocal should flow across await points
            Assert.False(interactionService.IsAvailable);
        }

        Assert.True(interactionService.IsAvailable);
    }

    [Fact]
    public void IsAvailable_NestedNonInteractiveScopes_RestoresPreviousValue()
    {
        var interactionService = CreateInteractionService();

        Assert.True(interactionService.IsAvailable);

        using (InteractionService.StartNonInteractiveScope())
        {
            Assert.False(interactionService.IsAvailable);

            using (InteractionService.StartNonInteractiveScope())
            {
                Assert.False(interactionService.IsAvailable);
            }

            // Inner scope disposed, but outer scope still active
            Assert.False(interactionService.IsAvailable);
        }

        Assert.True(interactionService.IsAvailable);
    }

    [Fact]
    public void IsAvailable_NullScopeDispose_DoesNotAffectOuterScope()
    {
        var interactionService = CreateInteractionService();

        Assert.True(interactionService.IsAvailable);

        using (InteractionService.StartNonInteractiveScope())
        {
            Assert.False(interactionService.IsAvailable);

            using var _ = default(InteractionService.NonInteractiveScope);

            Assert.False(interactionService.IsAvailable);
        }

        Assert.True(interactionService.IsAvailable);
    }

    [Fact]
    public async Task PromptInputAsync_ValidationCallbackInvalidData_ReturnErrors()
    {
        var interactionService = CreateInteractionService();

        var input = new InteractionInput { Name = "Value", Label = "Value", InputType = InputType.Text, };
        var resultTask = interactionService.PromptInputAsync(
            "Please provide", "please",
            input,
            new InputsDialogInteractionOptions
            {
                ValidationCallback = context =>
                {
                    // everything is invalid
                    context.AddValidationError(input, "Invalid value");
                    return Task.CompletedTask;
                }
            });

        var interaction = Assert.Single(interactionService.GetCurrentInteractions());
        Assert.False(interaction.CompletionTcs.Task.IsCompleted);
        Assert.Equal(Interaction.InteractionState.InProgress, interaction.State);

        await CompleteInteractionAsync(
            interactionService,
            interaction.InteractionId,
            new InteractionCompletionState { Complete = true, State = new[] { input } },
            inputs: [new InputDto("Value", string.Empty, InputType.Text)]);

        // The interaction should still be in progress due to validation error
        Assert.False(interaction.CompletionTcs.Task.IsCompleted);

        Assert.Collection(
            input.ValidationErrors,
            error => Assert.Equal("Invalid value", error));
    }

    [Fact]
    public async Task PromptInputsAsync_MissingRequiredData_ReturnErrors()
    {
        var interactionService = CreateInteractionService();

        var input = new InteractionInput { Name = "Value", Label = "Value", InputType = InputType.Text, Required = true };
        var resultTask = interactionService.PromptInputAsync("Please provide", "please", input);

        var interaction = Assert.Single(interactionService.GetCurrentInteractions());

        await CompleteInteractionAsync(
            interactionService,
            interaction.InteractionId,
            new InteractionCompletionState { Complete = true, State = new[] { input } },
            inputs: [new InputDto("Value", string.Empty, InputType.Text)]);

        // The interaction should still be in progress due to invalid data
        Assert.False(interaction.CompletionTcs.Task.IsCompleted);

        Assert.Collection(input.ValidationErrors,
            error => Assert.Equal("Value is required.", error));
    }

    [Fact]
    public async Task PromptInputsAsync_ChoiceHasNonOptionValue_ReturnErrors()
    {
        var interactionService = CreateInteractionService();

        var input = new InteractionInput { Name = "Value", Label = "Value", InputType = InputType.Choice, Options = [KeyValuePair.Create("first", "First option!"), KeyValuePair.Create("second", "Second option!")] };
        var resultTask = interactionService.PromptInputAsync("Please provide", "please", input);

        var interaction = Assert.Single(interactionService.GetCurrentInteractions());

        await CompleteInteractionAsync(
            interactionService,
            interaction.InteractionId,
            new InteractionCompletionState { Complete = true, State = new[] { input } },
            inputs: [new InputDto("Value", "not-in-options", InputType.Choice)]);

        // The interaction should still be in progress due to invalid data
        Assert.False(interaction.CompletionTcs.Task.IsCompleted);

        Assert.Collection(input.ValidationErrors,
            error => Assert.Equal("Value must be one of the provided options.", error));
    }

    [Fact]
    public async Task PromptInputsAsync_ChoiceHasNonOptionValueWithAllowCustomChoice_ReturnValue()
    {
        var interactionService = CreateInteractionService();

        var input = new InteractionInput { Name = "Value", Label = "Value", InputType = InputType.Choice, AllowCustomChoice = true, Options = [KeyValuePair.Create("first", "First option!"), KeyValuePair.Create("second", "Second option!")] };
        var resultTask = interactionService.PromptInputAsync("Please provide", "please", input);

        var interaction = Assert.Single(interactionService.GetCurrentInteractions());

        await CompleteInteractionAsync(
            interactionService,
            interaction.InteractionId,
            new InteractionCompletionState { Complete = true, State = new[] { input } },
            inputs: [new InputDto("Value", "not-in-options", InputType.Choice)]);

        var result = await resultTask.DefaultTimeout();
        Assert.False(result.Canceled);
        Assert.Equal("not-in-options", result.Data.Value);
    }

    [Fact]
    public async Task PromptInputsAsync_NumberHasNonNumberValue_ReturnErrors()
    {
        var interactionService = CreateInteractionService();

        var input = new InteractionInput { Name = "Value", Label = "Value", InputType = InputType.Number };
        var resultTask = interactionService.PromptInputAsync("Please provide", "please", input);

        var interaction = Assert.Single(interactionService.GetCurrentInteractions());

        await CompleteInteractionAsync(
            interactionService,
            interaction.InteractionId,
            new InteractionCompletionState { Complete = true, State = new[] { input } },
            inputs: [new InputDto("Value", "one", InputType.Number)]);

        // The interaction should still be in progress due to invalid data
        Assert.False(interaction.CompletionTcs.Task.IsCompleted);

        Assert.Collection(input.ValidationErrors,
            error => Assert.Equal("Value must be a valid number.", error));
    }

    [Fact]
    public async Task PromptInputsAsync_BooleanHasNonBooleanValue_ReturnErrors()
    {
        var interactionService = CreateInteractionService();

        var input = new InteractionInput { Name = "Value", Label = "Value", InputType = InputType.Boolean };
        var resultTask = interactionService.PromptInputAsync("Please provide", "please", input);

        var interaction = Assert.Single(interactionService.GetCurrentInteractions());

        await CompleteInteractionAsync(
            interactionService,
            interaction.InteractionId,
            new InteractionCompletionState { Complete = true, State = new[] { input } },
            inputs: [new InputDto("Value", "maybe", InputType.Number)]);

        // The interaction should still be in progress due to invalid data
        Assert.False(interaction.CompletionTcs.Task.IsCompleted);

        Assert.Collection(input.ValidationErrors,
            error => Assert.Equal("Value must be a valid boolean.", error));
    }

    [Theory]
    [InlineData(InputType.Text, null)]
    [InlineData(InputType.Text, 1)]
    [InlineData(InputType.Text, 10)]
    [InlineData(InputType.Text, InteractionHelpers.DefaultMaxLength)]
    [InlineData(InputType.SecretText, 10)]
    public async Task PromptInputsAsync_TextExceedsLimit_ReturnErrors(InputType inputType, int? maxLength)
    {
        await TextExceedsLimitCoreAsync(inputType, maxLength, success: true);
        await TextExceedsLimitCoreAsync(inputType, maxLength, success: false);

        static async Task TextExceedsLimitCoreAsync(InputType inputType, int? maxLength, bool success)
        {
            var interactionService = CreateInteractionService();

            var input = new InteractionInput { Name = "Value", Label = "Value", InputType = inputType, MaxLength = maxLength };
            var resultTask = interactionService.PromptInputAsync("Please provide", "please", input);

            var interaction = Assert.Single(interactionService.GetCurrentInteractions());
            var resolvedMaxLength = InteractionHelpers.GetMaxLength(maxLength);

            var newValue = new string('!', success ? resolvedMaxLength : resolvedMaxLength + 1);
            await CompleteInteractionAsync(
                interactionService,
                interaction.InteractionId,
                new InteractionCompletionState { Complete = true, State = new[] { input } },
                inputs: [new InputDto("Value", newValue, InputType.Text)]);

            if (!success)
            {
                // The interaction should still be in progress due to invalid data
                Assert.False(interaction.CompletionTcs.Task.IsCompleted);

                Assert.Collection(input.ValidationErrors,
                    error => Assert.Equal($"Value length exceeds {resolvedMaxLength} characters.", error));
            }
            else
            {
                Assert.True(interaction.CompletionTcs.Task.IsCompletedSuccessfully);
            }
        }
    }

    [Fact]
    public void InteractionInput_WithDescription_SetsProperties()
    {
        // Arrange & Act
        var input = new InteractionInput
        {
            Name = "TestLabel",
            Label = "Test Label",
            InputType = InputType.Text,
            Description = "Test description",
            EnableDescriptionMarkdown = false
        };

        // Assert
        Assert.Equal("Test Label", input.Label);
        Assert.Equal(InputType.Text, input.InputType);
        Assert.Equal("Test description", input.Description);
        Assert.False(input.EnableDescriptionMarkdown);
    }

    [Fact]
    public void InteractionInput_WithMarkdownDescription_SetsMarkupFlag()
    {
        // Arrange & Act
        var input = new InteractionInput
        {
            Name = "TestLabel",
            Label = "Test Label",
            InputType = InputType.Text,
            Description = "**Bold** description",
            EnableDescriptionMarkdown = true
        };

        // Assert
        Assert.Equal("**Bold** description", input.Description);
        Assert.True(input.EnableDescriptionMarkdown);
    }

    [Fact]
    public void InteractionInput_WithNullDescription_AllowsNullValue()
    {
        // Arrange & Act
        var input = new InteractionInput
        {
            Name = "TestLabel",
            Label = "Test Label",
            InputType = InputType.Text,
            Description = null,
            EnableDescriptionMarkdown = false
        };

        // Assert
        Assert.Null(input.Description);
        Assert.False(input.EnableDescriptionMarkdown);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(1, false)]
    [InlineData(int.MaxValue, false)]
    [InlineData(0, true)]
    [InlineData(-1, true)]
    [InlineData(int.MinValue, true)]
    public void InteractionInput_WithLengths_ErrorOnInvalid(int? length, bool invalid)
    {
        // Arrange & Act
        if (invalid)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => SetLength(length));
        }
        else
        {
            SetLength(length);
        }

        static void SetLength(int? length)
        {
            var input = new InteractionInput
            {
                Name = "TestLabel",
                Label = "Test Label",
                InputType = InputType.Text,
                MaxLength = length
            };
        }
    }

    [Fact]
    public void InteractionInputCollection_WithExplicitNames_AccessibleByName()
    {
        // Arrange
        var inputs = new List<InteractionInput>
        {
            new InteractionInput { Name = "Username", Label = "Username", InputType = InputType.Text },
            new InteractionInput { Name = "Password", Label = "Password", InputType = InputType.SecretText },
            new InteractionInput { Name = "RememberMe", Label = "Remember Me", InputType = InputType.Boolean }
        };

        // Act
        var collection = new InteractionInputCollection(inputs);

        // Assert
        Assert.Equal(3, collection.Count);
        Assert.Equal("Username", collection["Username"].Label);
        Assert.Equal("Password", collection["Password"].Label);
        Assert.Equal("Remember Me", collection["RememberMe"].Label);

        // Check names collection
        Assert.Contains("Username", collection.Names);
        Assert.Contains("Password", collection.Names);
        Assert.Contains("RememberMe", collection.Names);

        // Check TryGetByName
        Assert.True(collection.TryGetByName("Username", out var usernameInput));
        Assert.Equal("Username", usernameInput.Label);

        Assert.False(collection.TryGetByName("NonExistent", out var nonExistentInput));
        Assert.Null(nonExistentInput);

        Assert.True(collection.TryGetByName("RememberMe", out _));
        Assert.False(collection.TryGetByName("Remember Me", out _));

        // Check ContainsName
        Assert.True(collection.ContainsName("Username"));
        Assert.False(collection.ContainsName("NonExistent"));
    }

    [Fact]
    public void InteractionInputCollection_WithNames_AccessibleByName()
    {
        // Arrange
        var inputs = new List<InteractionInput>
        {
            new InteractionInput { Name = "UserName", Label = "User Name", InputType = InputType.Text },
            new InteractionInput { Name = "EmailAddress", Label = "Email Address", InputType = InputType.Text },
            new InteractionInput { Name = "Age", InputType = InputType.Number }
        };

        // Act
        var collection = new InteractionInputCollection(inputs);

        // Assert
        Assert.Equal(3, collection.Count);

        // Names should be accessible
        Assert.True(collection.ContainsName("UserName"));
        Assert.True(collection.ContainsName("EmailAddress"));
        Assert.True(collection.ContainsName("Age"));

        // Check that names are accessible
        Assert.Equal("User Name", collection["UserName"].Label);
        Assert.Equal("Email Address", collection["EmailAddress"].Label);
        Assert.Null(collection["Age"].Label); // No label specified, should use EffectiveLabel
        Assert.Equal("Age", collection["Age"].EffectiveLabel);

        // Check that the original inputs still work by index
        Assert.Equal("User Name", collection[0].Label);
        Assert.Equal("Email Address", collection[1].Label);
        Assert.Null(collection[2].Label);
    }

    [Fact]
    public void InteractionInputCollection_WithMixedNames_HandlesCorrectly()
    {
        // Arrange
        var inputs = new List<InteractionInput>
        {
            new InteractionInput { Name = "ExplicitName", Label = "Explicit", InputType = InputType.Text },
            new InteractionInput { Name = "GeneratedLabel", Label = "Generated Label", InputType = InputType.Text },
            new InteractionInput { Name = "AnotherExplicit", Label = "Another", InputType = InputType.Text }
        };

        // Act
        var collection = new InteractionInputCollection(inputs);

        // Assert
        Assert.Equal(3, collection.Count);

        // All names should work
        Assert.Equal("Explicit", collection["ExplicitName"].Label);
        Assert.Equal("Another", collection["AnotherExplicit"].Label);
        Assert.Equal("Generated Label", collection["GeneratedLabel"].Label);
    }

    [Fact]
    public void InteractionInputCollection_WithDuplicateNames_ThrowsException()
    {
        // Arrange
        var inputs = new List<InteractionInput>
        {
            new InteractionInput { Name = "Duplicate", Label = "First", InputType = InputType.Text },
            new InteractionInput { Name = "Duplicate", Label = "Second", InputType = InputType.Text }
        };

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => new InteractionInputCollection(inputs));
        Assert.Contains("Duplicate input name 'Duplicate' found", exception.Message);
    }

    [Fact]
    public void InteractionInputCollection_WithCaseInsensitiveNames_ThrowsException()
    {
        // Arrange
        var inputs = new List<InteractionInput>
        {
            new InteractionInput { Name = "Username", Label = "First", InputType = InputType.Text },
            new InteractionInput { Name = "USERNAME", Label = "Second", InputType = InputType.Text }
        };

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => new InteractionInputCollection(inputs));
        Assert.Contains("Duplicate input name 'USERNAME' found", exception.Message);
    }

    [Fact]
    public void InteractionInputCollection_WithUniqueNames_WorksCorrectly()
    {
        // Arrange
        var inputs = new List<InteractionInput>
        {
            new InteractionInput { Name = "Input1", InputType = InputType.Text },
            new InteractionInput { Name = "Input2", InputType = InputType.Text },
            new InteractionInput { Name = "Input3", InputType = InputType.Text }
        };

        // Act
        var collection = new InteractionInputCollection(inputs);

        // Assert
        Assert.Equal(3, collection.Count);

        // All names should be accessible
        Assert.True(collection.ContainsName("Input1"));
        Assert.True(collection.ContainsName("Input2"));
        Assert.True(collection.ContainsName("Input3"));

        // All should be accessible by their names
        Assert.NotNull(collection["Input1"]);
        Assert.NotNull(collection["Input2"]);
        Assert.NotNull(collection["Input3"]);

        // All should use name as effective label since no label is specified
        Assert.Equal("Input1", collection["Input1"].EffectiveLabel);
        Assert.Equal("Input2", collection["Input2"].EffectiveLabel);
        Assert.Equal("Input3", collection["Input3"].EffectiveLabel);
    }

    [Fact]
    public void InteractionInputCollection_WithValidNamesAndLabels_WorksCorrectly()
    {
        // Arrange
        var inputs = new List<InteractionInput>
        {
            new InteractionInput { Name = "SpecialInput", Label = "!@#$%^&*()", InputType = InputType.Text },
            new InteractionInput { Name = "EmptyLabel", Label = "", InputType = InputType.Text },
            new InteractionInput { Name = "WhitespaceLabel", Label = "   ", InputType = InputType.Text }
        };

        // Act
        var collection = new InteractionInputCollection(inputs);

        // Assert
        Assert.Equal(3, collection.Count);

        // All names should be accessible
        Assert.True(collection.Names.All(name => !string.IsNullOrWhiteSpace(name)));

        // All inputs should be accessible by their names
        foreach (var name in collection.Names)
        {
            Assert.NotNull(collection[name]);
        }

        // Labels should be preserved as specified, effective labels should fall back to names
        Assert.Equal("!@#$%^&*()", collection["SpecialInput"].Label);
        Assert.Equal("!@#$%^&*()", collection["SpecialInput"].EffectiveLabel);

        Assert.Equal("", collection["EmptyLabel"].Label);
        Assert.Equal("EmptyLabel", collection["EmptyLabel"].EffectiveLabel); // Falls back to name

        Assert.Equal("   ", collection["WhitespaceLabel"].Label);
        Assert.Equal("WhitespaceLabel", collection["WhitespaceLabel"].EffectiveLabel); // Falls back to name
    }

    [Fact]
    public void InteractionInputCollection_AccessByInvalidName_ThrowsKeyNotFoundException()
    {
        // Arrange
        var inputs = new List<InteractionInput>
        {
            new InteractionInput { Name = "Valid", Label = "Valid", InputType = InputType.Text }
        };
        var collection = new InteractionInputCollection(inputs);

        // Act & Assert
        var exception = Assert.Throws<KeyNotFoundException>(() => collection["Invalid"]);
        Assert.Contains("No input with name 'Invalid' was found", exception.Message);
    }

    [Fact]
    public async Task PromptInputsAsync_WithNamedInputs_ReturnsNamedCollection()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        var inputs = new List<InteractionInput>
        {
            new InteractionInput { Name = "Username", Label = "Username", InputType = InputType.Text },
            new InteractionInput { Name = "Password", Label = "Password", InputType = InputType.SecretText }
        };

        // Act
        var resultTask = interactionService.PromptInputsAsync("Login", "Please enter credentials", inputs);
        var interaction = Assert.Single(interactionService.GetCurrentInteractions());

        // Set values and complete
        await CompleteInteractionAsync(
            interactionService,
            interaction.InteractionId,
            new InteractionCompletionState { Complete = true, State = inputs },
            inputs: [
                new InputDto("Username", "testuser", InputType.Text),
                new InputDto("Password", "testpass", InputType.SecretText)
            ]);

        var result = await resultTask.DefaultTimeout();

        // Assert
        Assert.False(result.Canceled);
        Assert.NotNull(result.Data);

        var resultCollection = result.Data;
        Assert.Equal(2, resultCollection.Count);

        // Should be accessible by name
        Assert.Equal("testuser", resultCollection["Username"].Value);
        Assert.Equal("testpass", resultCollection["Password"].Value);

        // Should also be accessible by index for backward compatibility
        Assert.Equal("testuser", resultCollection[0].Value);
        Assert.Equal("testpass", resultCollection[1].Value);
    }

    [Fact]
    public async Task PromptInputsAsync_WithDynamicInput_NotDependant_LoadOnPrompt()
    {
        // Arrange
        var updateTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interactionService = CreateInteractionService();
        var subscription = interactionService.SubscribeInteractionUpdates();
        var updates = Channel.CreateUnbounded<Interaction>();
        var readTask = Task.Run(async () =>
        {
            await foreach (var interaction in subscription.WithCancellation(CancellationToken.None))
            {
                await updates.Writer.WriteAsync(interaction);
            }
        });

        var inputs = new List<InteractionInput>
        {
            new InteractionInput
            {
                Name = "Dynamic",
                InputType = InputType.Choice,
                DynamicLoading = new InputLoadOptions
                {
                    LoadCallback = async c =>
                    {
                        await updateTcs.Task;
                        c.Input.Options = [KeyValuePair.Create("loaded", "Loaded option")];
                    }
                }
            }
        };

        // Act
        var resultTask = interactionService.PromptInputsAsync("Login", "Please enter credentials", inputs);

        // Set values and complete
        var interaction = await updates.Reader.ReadAsync().DefaultTimeout();
        var inputsInteractionInfo = (Interaction.InputsInteractionInfo)interaction.InteractionInfo;

        Assert.True(inputsInteractionInfo.Inputs["Dynamic"].DynamicLoadingState!.Loading);
        Assert.Null(inputsInteractionInfo.Inputs["Dynamic"].Options);

        // Assert
        updateTcs.SetResult();

        interaction = await updates.Reader.ReadAsync().DefaultTimeout();
        inputsInteractionInfo = (Interaction.InputsInteractionInfo)interaction.InteractionInfo;

        Assert.False(inputsInteractionInfo.Inputs["Dynamic"].DynamicLoadingState!.Loading);
        Assert.Equal("loaded", inputsInteractionInfo.Inputs["Dynamic"].Options![0].Key);
    }

    [Fact]
    public async Task PromptInputsAsync_WithDynamicInput_Dependant_LoadOnDependantChange()
    {
        // Arrange
        var updateTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interactionService = CreateInteractionService();
        var subscription = interactionService.SubscribeInteractionUpdates();
        var updates = Channel.CreateUnbounded<Interaction>();
        var readTask = Task.Run(async () =>
        {
            await foreach (var interaction in subscription.WithCancellation(CancellationToken.None))
            {
                await updates.Writer.WriteAsync(interaction);
            }
        });

        var inputs = new List<InteractionInput>
        {
            new InteractionInput { Name = "Username", Label = "Username", InputType = InputType.Text },
            new InteractionInput
            {
                Name = "Dynamic",
                InputType = InputType.Choice,
                DynamicLoading = new InputLoadOptions
                {
                    LoadCallback = async c =>
                    {
                        await updateTcs.Task;
                        c.Input.Options = [KeyValuePair.Create("loaded", "Loaded option")];
                    },
                    DependsOnInputs = ["Username"]
                }
            }
        };

        // Act
        var resultTask = interactionService.PromptInputsAsync("Login", "Please enter credentials", inputs);

        // Set values and complete
        var interaction = await updates.Reader.ReadAsync().DefaultTimeout();
        var inputsInteractionInfo = (Interaction.InputsInteractionInfo)interaction.InteractionInfo;

        Assert.False(inputsInteractionInfo.Inputs["Dynamic"].DynamicLoadingState!.Loading);
        Assert.Null(inputsInteractionInfo.Inputs["Dynamic"].Options);

        await CompleteInteractionAsync(
            interactionService,
            interaction.InteractionId,
            new InteractionCompletionState { Complete = false, State = inputsInteractionInfo.Inputs },
            inputs: [new InputDto("Username", "testuser", InputType.Text)]).DefaultTimeout();

        // Assert
        interaction = await updates.Reader.ReadAsync().DefaultTimeout();
        inputsInteractionInfo = (Interaction.InputsInteractionInfo)interaction.InteractionInfo;

        Assert.True(inputsInteractionInfo.Inputs["Dynamic"].DynamicLoadingState!.Loading);

        updateTcs.SetResult();

        interaction = await updates.Reader.ReadAsync().DefaultTimeout();
        inputsInteractionInfo = (Interaction.InputsInteractionInfo)interaction.InteractionInfo;

        Assert.False(inputsInteractionInfo.Inputs["Dynamic"].DynamicLoadingState!.Loading);
        Assert.Equal("loaded", inputsInteractionInfo.Inputs["Dynamic"].Options![0].Key);
    }

    [Fact]
    public async Task ValidationContext_WithNamedInputs_AllowsNameAccess()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        var inputs = new List<InteractionInput>
        {
            new InteractionInput { Name = "Email", Label = "Email", InputType = InputType.Text, Required = true },
            new InteractionInput { Name = "Age", Label = "Age", InputType = InputType.Number, Required = true }
        };

        var validationCalled = false;
        var options = new InputsDialogInteractionOptions
        {
            ValidationCallback = context =>
            {
                validationCalled = true;

                // Should be able to access by name
                Assert.True(context.Inputs.ContainsName("Email"));
                Assert.True(context.Inputs.ContainsName("Age"));

                var emailInput = context.Inputs["Email"];
                var ageInput = context.Inputs["Age"];

                Assert.Equal("Email", emailInput.Label);
                Assert.Equal("Age", ageInput.Label);

                return Task.CompletedTask;
            }
        };

        // Act
        var resultTask = interactionService.PromptInputsAsync("Validation Test", "Test validation", inputs, options);
        var interaction = Assert.Single(interactionService.GetCurrentInteractions());

        await CompleteInteractionAsync(
            interactionService,
            interaction.InteractionId,
            new InteractionCompletionState { Complete = true, State = inputs },
            inputs: [
                new InputDto("Email", "test@example.com", InputType.Text),
                new InputDto("Age", "25", InputType.Number)
            ]);

        var result = await resultTask.DefaultTimeout();

        // Assert
        Assert.True(validationCalled);
        Assert.False(result.Canceled);
    }

    [Fact]
    public async Task DependsOn_DoesNotExist_Error()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        var inputs = new List<InteractionInput>
        {
            new InteractionInput
            {
                Name = "Choice",
                Label = "Choice",
                InputType = InputType.Choice,
                Required = true,
                DynamicLoading = new InputLoadOptions
                {
                    DependsOnInputs = ["DoesNotExist"],
                    LoadCallback = c => Task.FromResult<IReadOnlyList<KeyValuePair<string, string>>>(new Dictionary<string, string>
                    {
                        ["option1"] = "Option 1",
                        ["option2"] = "Option 2"
                    }.ToList())
                }
            },
            new InteractionInput { Name = "Age", Label = "Age", InputType = InputType.Number, Required = true }
        };

        // Act
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => interactionService.PromptInputsAsync("Validation Test", "Test validation", inputs));

        // Assert
        Assert.NotNull(ex);
    }

    [Fact]
    public async Task DependsOn_LaterInput_Error()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        var inputs = new List<InteractionInput>
        {
            new InteractionInput
            {
                Name = "Choice",
                Label = "Choice",
                InputType = InputType.Choice,
                Required = true,
                DynamicLoading = new InputLoadOptions
                {
                    DependsOnInputs = ["Age"],
                    LoadCallback = c => Task.FromResult<IReadOnlyList<KeyValuePair<string, string>>>(new Dictionary<string, string>
                    {
                        ["option1"] = "Option 1",
                        ["option2"] = "Option 2"
                    }.ToList())
                }
            },
            new InteractionInput { Name = "Age", Label = "Age", InputType = InputType.Number, Required = true }
        };

        // Act
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => interactionService.PromptInputsAsync("Validation Test", "Test validation", inputs));

        // Assert
        Assert.NotNull(ex);
    }

    private static async Task CompleteInteractionAsync(InteractionService interactionService, int interactionId, InteractionCompletionState state, List<DashboardServiceData.InputDto>? inputs = null)
    {
        await interactionService.ProcessInteractionFromClientAsync(
            interactionId,
            (interaction, serviceProvider, logger) =>
            {
                if (interaction.InteractionInfo is Interaction.InputsInteractionInfo inputsInfo)
                {
                    if (inputs == null)
                    {
                        throw new InvalidOperationException("Inputs should be specified when completing input interaction");
                    }

                    DashboardServiceData.ProcessInputs(
                        serviceProvider,
                        logger,
                        inputsInfo,
                        inputs,
                        !state.Complete,
                        interaction.CancellationToken);
                }
                return state;
            },
            CancellationToken.None);
    }

    [Fact]
    public void RegisterPage_StartPageInteraction_AddsInteraction()
    {
        // Arrange
        var interactionService = CreateInteractionService();

        // Act
        var registration = interactionService.RegisterPage("my-page", new PageContext
        {
            Title = "My Page",
            OnVisit = _ => Task.CompletedTask
        });

        var startedPage = interactionService.StartPageInteraction("my-page", "session-1", new Dictionary<string, string>(), CancellationToken.None);

        // Assert
        Assert.NotNull(startedPage);
        var interaction = Assert.Single(interactionService.GetCurrentInteractions());
        Assert.Equal("My Page", interaction.Title);
        var pageInfo = Assert.IsType<Interaction.PageInteractionInfo>(interaction.InteractionInfo);
        Assert.Equal("my-page", pageInfo.Route);
        Assert.Equal("My Page", pageInfo.PageContext.Title);

        registration.Dispose();
    }

    [Fact]
    public void RegisterPage_Dispose_RemovesRegistrationAndActiveInteraction()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        var registration = interactionService.RegisterPage("my-page", new PageContext
        {
            Title = "My Page"
        });
        var startedPage = interactionService.StartPageInteraction("my-page", "session-1", new Dictionary<string, string>(), CancellationToken.None);
        Assert.NotNull(startedPage);
        Assert.Single(interactionService.GetCurrentInteractions());

        // Act
        registration.Dispose();

        // Assert
        Assert.Empty(interactionService.GetCurrentInteractions());
        Assert.Null(interactionService.StartPageInteraction("my-page", "session-2", new Dictionary<string, string>(), CancellationToken.None));
    }

    [Fact]
    public void RegisterPage_NullRoute_ThrowsArgumentNullException()
    {
        var interactionService = CreateInteractionService();

        Assert.Throws<ArgumentNullException>(() => interactionService.RegisterPage(null!, new PageContext()));
    }

    [Fact]
    public void RegisterPage_NullContext_ThrowsArgumentNullException()
    {
        var interactionService = CreateInteractionService();

        Assert.Throws<ArgumentNullException>(() => interactionService.RegisterPage("route", null!));
    }

    [Fact]
    public void RegisterPage_WithoutTitle_UseRouteAsTitle()
    {
        // Arrange
        var interactionService = CreateInteractionService();

        // Act
        var registration = interactionService.RegisterPage("my-route", new PageContext());
        var startedPage = interactionService.StartPageInteraction("my-route", "session-1", new Dictionary<string, string>(), CancellationToken.None);

        // Assert
        var interaction = Assert.Single(interactionService.GetCurrentInteractions());
        Assert.NotNull(startedPage);
        Assert.Equal("my-route", interaction.Title);

        registration.Dispose();
    }

    [Fact]
    public async Task StartPageInteraction_InvokesOnVisitCallback()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        var visitCalled = new TaskCompletionSource<PageVisitContext>();

        interactionService.RegisterPage("test-page", new PageContext
        {
            Title = "Test",
            OnVisit = ctx =>
            {
                visitCalled.TrySetResult(ctx);
                return Task.CompletedTask;
            }
        });

        var queryParams = new Dictionary<string, string> { ["key"] = "value" };

        // Act
        var startedPage = interactionService.StartPageInteraction("test-page", "session-1", queryParams, CancellationToken.None);

        // Assert
        Assert.NotNull(startedPage);
        var context = await visitCalled.Task.DefaultTimeout();
        Assert.Equal("session-1", context.SessionId);
        Assert.Equal("value", context.QueryParameters["key"]);
    }

    [Fact]
    public void StartPageInteraction_UnknownRoute_ReturnsNull()
    {
        var interactionService = CreateInteractionService();

        var result = interactionService.StartPageInteraction("missing-page", "session-1", new Dictionary<string, string>(), CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(interactionService.GetCurrentInteractions());
    }

    [Fact]
    public async Task StartPageInteraction_SendMarkdown_StoresContent()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        Func<string, CancellationToken, Task>? capturedSendMarkdown = null;
        var markdownSent = new TaskCompletionSource();

        interactionService.RegisterPage("test-page", new PageContext
        {
            Title = "Test",
            OnVisit = async ctx =>
            {
                capturedSendMarkdown = ctx.SendMarkdownAsync;
                await ctx.SendMarkdownAsync("# Hello", ctx.CancellationToken);
                markdownSent.SetResult();
            }
        });

        // Act
        var startedPage = interactionService.StartPageInteraction("test-page", "session-1", new Dictionary<string, string>(), CancellationToken.None);

        // Assert
        Assert.NotNull(startedPage);
        await markdownSent.Task.DefaultTimeout();
        Assert.NotNull(capturedSendMarkdown);
        var interaction = Assert.Single(interactionService.GetCurrentInteractions());
        var pageInfo = (Interaction.PageInteractionInfo)interaction.InteractionInfo;
        Assert.Equal("# Hello", pageInfo.Session.Markdown);
    }

    [Fact]
    public async Task ProcessInteractionFromClientAsync_CompletesPageInteraction_CancelsVisitorToken()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        var visitTokenCancelled = new TaskCompletionSource<bool>();

        interactionService.RegisterPage("test-page", new PageContext
        {
            Title = "Test",
            OnVisit = async ctx =>
            {
                ctx.CancellationToken.Register(() => visitTokenCancelled.TrySetResult(true));
                try
                {
                    await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Expected.
                }
            }
        });

        var startedPage = interactionService.StartPageInteraction("test-page", "session-1", new Dictionary<string, string>(), CancellationToken.None);
        Assert.NotNull(startedPage);

        // Give the visit callback time to register its cancellation handler.
        await Task.Delay(50);

        // Act
        await interactionService.ProcessInteractionFromClientAsync(
            startedPage.InteractionId,
            (_, _, _) => new InteractionCompletionState { Complete = true },
            CancellationToken.None);

        // Assert
        Assert.True(await visitTokenCancelled.Task.DefaultTimeout());
        Assert.Empty(interactionService.GetCurrentInteractions());
    }

    [Fact]
    public void RegisterMenuButton_AddsInteraction()
    {
        // Arrange
        var interactionService = CreateInteractionService();

        // Act
        var registration = interactionService.RegisterMenuButton(new MenuButtonOptions
        {
            IconName = "Home",
            Text = "Go Home",
            Tooltip = "Navigate to home",
            Url = "/pages/home"
        });

        // Assert
        var interaction = Assert.Single(interactionService.GetCurrentInteractions());
        Assert.Equal("Go Home", interaction.Title);
        var menuInfo = Assert.IsType<Interaction.MenuButtonInteractionInfo>(interaction.InteractionInfo);
        Assert.Equal("Home", menuInfo.Options.IconName);
        Assert.Equal("Go Home", menuInfo.Options.Text);
        Assert.Equal("Navigate to home", menuInfo.Options.Tooltip);
        Assert.Equal("/pages/home", menuInfo.Options.Url);

        registration.Dispose();
    }

    [Fact]
    public void RegisterMenuButton_Dispose_RemovesInteraction()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        var registration = interactionService.RegisterMenuButton(new MenuButtonOptions
        {
            IconName = "Home",
            Text = "Go Home",
            Tooltip = "Navigate to home",
            Url = "/pages/home"
        });
        Assert.Single(interactionService.GetCurrentInteractions());

        // Act
        registration.Dispose();

        // Assert
        Assert.Empty(interactionService.GetCurrentInteractions());
    }

    [Fact]
    public void RegisterMenuButton_NullOptions_ThrowsArgumentNullException()
    {
        var interactionService = CreateInteractionService();

        Assert.Throws<ArgumentNullException>(() => interactionService.RegisterMenuButton(null!));
    }

    [Fact]
    public void RegisterPage_DashboardDisabled_ThrowsInvalidOperationException()
    {
        var interactionService = CreateInteractionService(options: new DistributedApplicationOptions { DisableDashboard = true });

        Assert.Throws<InvalidOperationException>(() => interactionService.RegisterPage("route", new PageContext()));
    }

    [Fact]
    public void RegisterMenuButton_DashboardDisabled_ThrowsInvalidOperationException()
    {
        var interactionService = CreateInteractionService(options: new DistributedApplicationOptions { DisableDashboard = true });

        Assert.Throws<InvalidOperationException>(() => interactionService.RegisterMenuButton(new MenuButtonOptions
        {
            IconName = "Home",
            Text = "Home",
            Tooltip = "Home",
            Url = "/pages/home"
        }));
    }

    [Fact]
    public void RegisterPage_MultiplePages_AllTracked()
    {
        // Arrange
        var interactionService = CreateInteractionService();

        // Act
        var reg1 = interactionService.RegisterPage("page-1", new PageContext { Title = "Page 1" });
        var reg2 = interactionService.RegisterPage("page-2", new PageContext { Title = "Page 2" });

        // Assert
        Assert.Empty(interactionService.GetCurrentInteractions());

        var page1 = interactionService.StartPageInteraction("page-1", "session-1", new Dictionary<string, string>(), CancellationToken.None);
        var page2 = interactionService.StartPageInteraction("page-2", "session-2", new Dictionary<string, string>(), CancellationToken.None);

        Assert.NotNull(page1);
        Assert.NotNull(page2);
        Assert.Equal(2, interactionService.GetCurrentInteractions().Count);

        reg1.Dispose();
        Assert.Single(interactionService.GetCurrentInteractions());

        reg2.Dispose();
        Assert.Empty(interactionService.GetCurrentInteractions());
    }

    [Fact]
    public void RegisterPage_DuplicateRoute_ThrowsInvalidOperationException()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        interactionService.RegisterPage("my-page", new PageContext { Title = "First" });

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() =>
            interactionService.RegisterPage("my-page", new PageContext { Title = "Second" }));
        Assert.Contains("my-page", ex.Message);
    }

    [Fact]
    public void RegisterPage_DuplicateRoute_CaseInsensitive_ThrowsInvalidOperationException()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        interactionService.RegisterPage("My-Page", new PageContext { Title = "First" });

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            interactionService.RegisterPage("my-page", new PageContext { Title = "Second" }));
    }

    [Fact]
    public void RegisterPage_SameRouteAfterDispose_Succeeds()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        var reg = interactionService.RegisterPage("my-page", new PageContext { Title = "First" });
        reg.Dispose();

        // Act — should not throw since the first registration was disposed.
        var reg2 = interactionService.RegisterPage("my-page", new PageContext { Title = "Second" });
        var startedPage = interactionService.StartPageInteraction("my-page", "session-1", new Dictionary<string, string>(), CancellationToken.None);

        // Assert
        var interaction = Assert.Single(interactionService.GetCurrentInteractions());
        Assert.NotNull(startedPage);
        Assert.Equal("Second", interaction.Title);
        reg2.Dispose();
    }

    [Fact]
    public async Task RegisterAsset_OnGet_WritesToStream()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        using var registration = interactionService.RegisterAsset("scripts/app.js", "application/javascript", new AssetContext
        {
            OnGet = async context =>
            {
                await context.Stream.WriteAsync("console.log('hello');"u8.ToArray(), context.CancellationToken);
            }
        });

        using var stream = new MemoryStream();

        // Act
        var found = await interactionService.WriteAssetAsync("scripts/app.js", stream, CancellationToken.None);

        // Assert
        Assert.True(found);
        Assert.Equal("console.log('hello');", System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public async Task RegisterAsset_ByteArrayOverload_UsesRegisteredContent()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        var content = "body { color: red; }"u8.ToArray();
        using var registration = interactionService.RegisterAsset("styles/site.css", "text/css", content);

        using var stream = new MemoryStream();

        // Act
        var found = await interactionService.WriteAssetAsync("styles/site.css", stream, CancellationToken.None);

        // Assert
        Assert.True(found);
        Assert.Equal("body { color: red; }", System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public void RegisterAsset_DuplicateRoute_ThrowsInvalidOperationException()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        using var registration = interactionService.RegisterAsset("logo.svg", "image/svg+xml", []);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => interactionService.RegisterAsset("logo.svg", "image/svg+xml", []));
    }

    [Fact]
    public async Task RegisterAsset_SameRouteAfterDispose_Succeeds()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        var reg1 = interactionService.RegisterAsset("downloads/file.txt", "text/plain", "first"u8.ToArray());
        reg1.Dispose();

        // Act
        using var reg2 = interactionService.RegisterAsset("downloads/file.txt", "text/plain", "second"u8.ToArray());
        using var stream = new MemoryStream();
        var found = await interactionService.WriteAssetAsync("downloads/file.txt", stream, CancellationToken.None);

        // Assert
        Assert.True(found);
        Assert.Equal("second", System.Text.Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public async Task StartPageInteraction_MultipleSessionsSendMarkdown_AllSessionsStored()
    {
        // Arrange
        var interactionService = CreateInteractionService();
        var session1Ready = new TaskCompletionSource();
        var session2Ready = new TaskCompletionSource();
        var continueSignal = new TaskCompletionSource();

        interactionService.RegisterPage("test-page", new PageContext
        {
            Title = "Test",
            OnVisit = async ctx =>
            {
                if (ctx.SessionId == "session-1")
                {
                    session1Ready.SetResult();
                }
                else
                {
                    session2Ready.SetResult();
                }

                await continueSignal.Task;
                await ctx.SendMarkdownAsync($"# Content from {ctx.SessionId}", ctx.CancellationToken);
            }
        });

        // Act — start two visits concurrently (fire-and-forget, like the real code does).
        var startedPage1 = interactionService.StartPageInteraction("test-page", "session-1", new Dictionary<string, string>(), CancellationToken.None);
        var startedPage2 = interactionService.StartPageInteraction("test-page", "session-2", new Dictionary<string, string>(), CancellationToken.None);

        // Wait for both callbacks to start.
        Assert.NotNull(startedPage1);
        Assert.NotNull(startedPage2);
        await session1Ready.Task.DefaultTimeout();
        await session2Ready.Task.DefaultTimeout();

        // Let both sessions send markdown concurrently.
        continueSignal.SetResult();

        // Give the callbacks time to complete.
        await Task.Delay(100);

        // Assert — both sessions should have their content stored.
        var interactions = interactionService.GetCurrentInteractions();
        var pageInfo1 = Assert.IsType<Interaction.PageInteractionInfo>(interactions.Single(i => i.InteractionId == startedPage1.InteractionId).InteractionInfo);
        var pageInfo2 = Assert.IsType<Interaction.PageInteractionInfo>(interactions.Single(i => i.InteractionId == startedPage2.InteractionId).InteractionInfo);
        Assert.Equal("# Content from session-1", pageInfo1.Session.Markdown);
        Assert.Equal("# Content from session-2", pageInfo2.Session.Markdown);
    }

    [Fact]
    public async Task StartPageInteraction_ConcurrentSendMarkdown_DoesNotCorruptState()
    {
        // Arrange — stress test to verify the lock protects state under concurrent writes.
        var interactionService = CreateInteractionService();
        const int writesPerSession = 50;
        var visitReady = new TaskCompletionSource<PageVisitContext>();

        interactionService.RegisterPage("stress-page", new PageContext
        {
            Title = "Stress",
            OnVisit = async ctx =>
            {
                visitReady.SetResult(ctx);

                // Keep the visit alive until cancelled.
                try
                {
                    await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
                }
                catch (OperationCanceledException)
                {
                }
            }
        });

        var startedPage = interactionService.StartPageInteraction("stress-page", "session-1", new Dictionary<string, string>(), CancellationToken.None);
        Assert.NotNull(startedPage);
        var ctx = await visitReady.Task.DefaultTimeout();

        // Act — concurrent updates for the same active session should not corrupt state.
        var tasks = new List<Task>();
        for (var i = 0; i < writesPerSession; i++)
        {
            var update = i;
            tasks.Add(Task.Run(async () =>
            {
                await ctx.SendMarkdownAsync($"Update {update}", ctx.CancellationToken);
            }));
        }

        await Task.WhenAll(tasks).DefaultTimeout();

        // Assert — no exception was thrown and the session has one of the updates.
        var interaction = Assert.Single(interactionService.GetCurrentInteractions());
        var pageInfo = Assert.IsType<Interaction.PageInteractionInfo>(interaction.InteractionInfo);
        Assert.StartsWith("Update ", pageInfo.Session.Markdown);
    }

    private static InteractionService CreateInteractionService(DistributedApplicationOptions? options = null)
    {
        var configuration = new ConfigurationBuilder().Build();
        return new InteractionService(
            NullLogger<InteractionService>.Instance,
            options ?? new DistributedApplicationOptions(),
            new ServiceCollection().BuildServiceProvider(),
            configuration);
    }
}

#pragma warning restore ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
