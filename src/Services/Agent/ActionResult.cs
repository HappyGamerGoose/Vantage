// SPDX-License-Identifier: MIT

namespace Vantage.Services.Agent;

public enum ActionOutcome { Success, Failed, Done, FailedFatal }

public sealed record ActionResult(ActionOutcome Outcome, string Description);
