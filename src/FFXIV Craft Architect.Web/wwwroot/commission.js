import {
    CommissionBriefApiClient,
    CommissionClientError,
    LodestoneExistenceClient,
    ParticipantAccessStore,
    clearCapabilityFragment,
    createCommandAuthorization,
    readCapabilityFragment,
    resolveParticipantPreworkChoices
} from "./commission-client.js";

const byId = id => document.getElementById(id);
const formatNumber = value => Number(value).toLocaleString();
const formatGil = value => `${Math.round(Number(value)).toLocaleString()}g`;
const formatDate = value => new Date(value).toLocaleString(undefined, {
    year: "numeric",
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit"
});
const formatShortDate = value => new Date(value).toLocaleString(undefined, {
    month: "short",
    day: "numeric",
    hour: "numeric",
    minute: "2-digit"
});

const state = {
    publicId: null,
    client: null,
    lodestone: new LodestoneExistenceClient(),
    store: null,
    capabilities: null,
    access: null,
    projection: null,
    projectionStream: null,
    projectionRefreshWaiters: [],
    busy: false,
    selectedLodestone: null
};

const activityLabels = {
    CommissionOpened: "Commission opened",
    ClaimAccepted: "Commission claimed",
    ClaimRejected: "Claim rejected",
    ClaimReleased: "Claim released",
    ClaimRecovered: "Participant access recovered",
    ProvisionalIdentitySubmitted: "Character submitted",
    ProvisionalIdentityConfirmed: "Identity confirmed",
    ProvisionalIdentityRejected: "Identity rejected",
    PaymentPolicyChangeRequested: "Payment change requested",
    PaymentPolicyChangeAccepted: "Payment change accepted",
    PaymentPolicyChangeRefused: "Payment change refused",
    TermsAcknowledged: "Updated terms accepted",
    PaymentClearanceRecorded: "Payment recorded",
    TermsAmended: "Commission terms revised",
    PaymentSentRecorded: "Commissioner marked payment sent",
    PaymentReceivedConfirmed: "Crafter confirmed payment received",
    PaymentAttestationRetracted: "Payment confirmation retracted",
    CompanyMaterialsReady: "Company materials ready",
    CompanyMaterialsReceived: "Company materials received",
    WorkClearanceAchieved: "Work cleared",
    ProgressReported: "Progress reported",
    CommentAdded: "Operational update",
    DeliveryReadinessDeclared: "Ready for delivery",
    DeliveryReadinessWithdrawn: "Readiness withdrawn",
    DeliveryReturnedToWork: "Returned to work",
    DeliveryAccepted: "Delivery accepted",
    SettlementRecorded: "Settlement recorded",
    CommissionCanceled: "Commission canceled",
    CommissionClosed: "Commission closed",
    CommissionPublicationRevoked: "Public brief revoked",
    ParticipantRecoveryIssued: "Recovery access issued",
    ParticipantRecoveryRedeemed: "Recovery access redeemed",
    MigratedFromTradeOrder: "Commission imported",
    MigratedTradeOrderHistory: "Earlier history imported"
};

const gateLabels = {
    NotRequired: "Not required",
    Pending: "Pending",
    Satisfied: "Satisfied"
};

function setText(id, value) {
    byId(id).textContent = value ?? "";
}

function element(tag, className = null, text = null) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text != null) node.textContent = text;
    return node;
}

function appendTextRow(target, label, value) {
    const row = element("div", "term-row");
    row.append(element("span", null, label), element("strong", null, value));
    target.append(row);
}

function createButton(label, onClick, className = "button") {
    const control = element("button", className, label);
    control.type = "button";
    control.addEventListener("click", event => {
        Promise.resolve(onClick(event)).catch(caught => {
            showNotice("Action unavailable", caught.message);
        });
    });
    return control;
}

function createField(label, input) {
    const field = element("label", "field");
    field.append(element("span", null, label), input);
    return field;
}

function createInput(type, name, options = {}) {
    const input = document.createElement("input");
    input.type = type;
    input.name = name;
    if (options.required) input.required = true;
    if (options.maxLength) input.maxLength = options.maxLength;
    if (options.min != null) input.min = String(options.min);
    if (options.max != null) input.max = String(options.max);
    if (options.value != null) input.value = String(options.value);
    if (options.placeholder) input.placeholder = options.placeholder;
    if (options.autocomplete) input.autocomplete = options.autocomplete;
    return input;
}

function createTextarea(name, options = {}) {
    const input = document.createElement("textarea");
    input.name = name;
    if (options.required) input.required = true;
    if (options.maxLength) input.maxLength = options.maxLength;
    if (options.placeholder) input.placeholder = options.placeholder;
    return input;
}

function createSelect(name, values) {
    const select = document.createElement("select");
    select.name = name;
    for (const [value, label] of values) {
        const option = document.createElement("option");
        option.value = value;
        option.textContent = label;
        select.append(option);
    }
    return select;
}

function showToast(message) {
    const toast = byId("toast");
    toast.textContent = message;
    toast.classList.add("is-visible");
    window.setTimeout(() => toast.classList.remove("is-visible"), 2200);
}

function showNotice(title, body) {
    setText("noticeTitle", title);
    setText("noticeBody", body);
    byId("notice").hidden = false;
}

function clearNotice() {
    byId("notice").hidden = true;
}

function setBusy(busy) {
    state.busy = busy;
    document.querySelectorAll("button, input, select, textarea").forEach(control => {
        control.disabled = busy || control.dataset.permanentDisabled === "true";
    });
    if (!busy) {
        const waiters = state.projectionRefreshWaiters.splice(0);
        waiters.forEach(resolve => resolve());
    }
}

function showFatal(error) {
    setText("statusChip", "Unavailable");
    setText("messageTitle", "Company commission unavailable");
    setText("messageBody", error instanceof Error
        ? error.message
        : "The canonical brief could not be loaded.");
    byId("briefShell").hidden = true;
    byId("briefMessage").hidden = false;
}

async function load() {
    const publicId = new URLSearchParams(window.location.search).get("id");
    if (!publicId) {
        throw new CommissionClientError("This commission link is incomplete.", "missing-public-id");
    }

    state.publicId = publicId;
    state.capabilities = readCapabilityFragment();
    state.store = new ParticipantAccessStore(publicId);
    try {
        state.access = state.store.load();
    } catch (caught) {
        const canReplaceDamagedAccess = caught.code === "invalid-saved-access" &&
            Boolean(state.capabilities.claimCapability || state.capabilities.recoveryCapability);
        if (!canReplaceDamagedAccess) throw caught;
        state.store.discard();
        state.access = null;
    }
    state.client = new CommissionBriefApiClient(publicId);
    const hasAuthorityFragment = Boolean(
        state.capabilities.claimCapability || state.capabilities.recoveryCapability);
    const loadSecret = state.access?.pending || !hasAuthorityFragment
        ? state.access?.participantSecret ?? null
        : null;
    try {
        state.projection = await state.client.load(loadSecret);
    } catch (caught) {
        const pendingAuthorityIsNotInstalled =
            caught instanceof CommissionClientError &&
            caught.status === 401 &&
            Boolean(loadSecret && state.access?.pending);
        if (!pendingAuthorityIsNotInstalled) throw caught;
        state.projection = await state.client.load();
    }

    if (state.projection.public.publicBriefId !== publicId) {
        throw new CommissionClientError(
            "The commission service returned a different public brief.",
            "public-id-mismatch");
    }
    if (state.projection.kind === "participant" && state.access?.pending) {
        state.store.completeAuthorityExchange(state.access);
        state.access = state.store.load();
        clearCapabilityFragment();
        state.capabilities = {
            claimCapability: null,
            recoveryCapability: null,
            recoveryGrantId: null
        };
    }

    render();
    byId("briefMessage").hidden = true;
    byId("briefShell").hidden = false;
    startProjectionWatch();
}

async function reloadProjection(successMessage = null) {
    state.access = state.store.load();
    state.projection = await state.client.load(state.access?.participantSecret ?? null);
    render();
    if (successMessage) showToast(successMessage);
}

function startProjectionWatch() {
    state.projectionStream?.stop();
    state.projectionStream = state.client.watch(
        state.projection?.kind === "participant"
            ? state.access?.participantSecret ?? null
            : null,
        state.projection?.projectionTag ?? null,
        refreshProjectionFromStream,
        (connected, message) => {
            if (!connected && message?.includes("authority is no longer available")) {
                void refreshProjectionFromStream(null, true);
            }
        });
}

async function refreshProjectionFromStream(expectedTag = null, authorityChanged = false) {
    await waitUntilProjectionRefreshIsSafe();

    state.access = state.store.load();
    let next;
    try {
        next = await state.client.load(state.access?.participantSecret ?? null);
    } catch (caught) {
        if (!authorityChanged || caught?.status !== 401) throw caught;
        next = await state.client.load();
    }

    state.projection = next;
    render();
    if (authorityChanged && next.kind === "anonymous") {
        startProjectionWatch();
    }
    return next.projectionTag;
}

async function waitUntilProjectionRefreshIsSafe() {
    while (true) {
        if (state.busy) {
            await new Promise(resolve => state.projectionRefreshWaiters.push(resolve));
            continue;
        }

        const active = document.activeElement;
        const editing = active instanceof HTMLElement &&
            active.matches("input, textarea, select") &&
            Boolean(active.closest("form"));
        if (!editing) {
            return;
        }

        await new Promise(resolve => active.addEventListener("blur", resolve, { once: true }));
    }
}

function render() {
    clearNotice();
    const projection = state.projection;
    const brief = projection.public;
    const terms = brief.terms;

    document.title = `${brief.title} — Company Commission`;
    setText("statusChip", formatStatus(brief));
    setText(
        "briefIdentity",
        `${brief.companyDisplayName} · Commission ${brief.reference} · Terms v${terms.version}`);
    setText("briefTitle", brief.title);
    setText("briefSubtitle", buildSubtitle(projection));
    setText("totalPayment", formatGil(terms.payment.total));

    if (brief.isTestFixture) {
        showNotice(
            "TEST COMMISSION - CLAIMING DISABLED",
            "This visible commission exists only for workflow and timeline verification. No crafter can claim it.");
    }

    renderNextStep();
    renderGates();
    renderOutputs();
    renderMaterials();
    renderTerms();
    renderAccess();
    renderActivity();
    renderCommentForm();
}

function buildSubtitle(projection) {
    if (projection.kind === "participant" && projection.provisionalCrafter) {
        const crafter = projection.provisionalCrafter;
        return `${crafter.characterName} · ${crafter.homeWorld} · ${formatDeliveryInstructions(projection.public.terms.deliveryInstructions)}`;
    }
    return formatDeliveryInstructions(projection.public.terms.deliveryInstructions);
}

function formatStatus(brief) {
    if (brief.isTestFixture) return "TEST - NOT CLAIMABLE";
    if (brief.viewState === "Revoked") return "REVOKED";
    if (brief.status === "Canceled") return "CANCELED";
    if (brief.closed) return "COMPLETED";
    if (brief.status === "Completed") return "DELIVERY ACCEPTED";
    if (brief.status === "AwaitingDelivery") return "READY FOR DELIVERY";
    if (brief.status === "InProgress") return "CRAFTING";
    if (!brief.isClaimed) return "OPEN - ONE CLAIM SLOT";
    if (brief.gates.identity === "Pending") return "CLAIMED - IDENTITY REVIEW";
    return brief.clearedToWork ? "CRAFTING" : "ASSIGNED - PRE-WORK";
}

function renderNextStep() {
    const next = resolveNextStep();
    const container = byId("nextStep");
    container.className = `next-step ${next.tone ? `is-${next.tone}` : ""}`.trim();
    setText("nextStepTitle", next.title);
    setText("nextStepBody", next.body);

    const actions = byId("nextStepActions");
    actions.replaceChildren();
    const form = byId("nextStepForm");
    form.replaceChildren();
    form.hidden = true;
    for (const action of next.actions ?? []) {
        actions.append(createButton(
            action.label,
            action.run,
            action.primary ? "button primary" : "button"));
    }
}

function resolveNextStep() {
    const projection = state.projection;
    const brief = projection.public;
    const pending = state.access?.pending;

    if (brief.isTestFixture) {
        return {
            title: "Test commission - no action required",
            body: "This fixture exercises the commission timeline and Discord projection. Claiming is disabled by the commission service.",
            tone: "blocked"
        };
    }
    if (brief.viewState !== "Published") {
        return {
            title: brief.viewState === "Revoked" ? "This public brief was revoked" : "This commission is not open",
            body: "No claim or participant command is available from this view.",
            tone: "blocked"
        };
    }
    if (brief.status === "Completed" && projection.kind === "participant") {
        const settlement = projection.settlementPayment;
        if (brief.settlementState === "Pending" && !settlement?.crafterReceived) {
            return {
                title: "Confirm final payment",
                body: `Confirm receipt of ${formatGil(brief.terms.payment.total)} against terms v${brief.terms.version}. Settlement completes only after both parties attest.`,
                tone: "waiting",
                actions: [{
                    label: `I received ${formatGil(brief.terms.payment.total)}`,
                    primary: true,
                    run: openSettlementReceiptForm
                }]
            };
        }
        if (brief.settlementState === "Pending") {
            return {
                title: "Waiting for commissioner confirmation",
                body: "You confirmed receipt of the final payment. Settlement completes when the commissioner records the same exchange.",
                tone: "waiting",
                actions: [{
                    label: "Retract my confirmation",
                    run: openSettlementRetractionForm
                }]
            };
        }
        if (brief.settlementState === "Satisfied") {
            return {
                title: "Commission complete",
                body: "Delivery and the two-party settlement confirmation are complete.",
                tone: "blocked",
                actions: settlement?.crafterReceived ? [{
                    label: "Correct my receipt confirmation",
                    run: openSettlementRetractionForm
                }] : []
            };
        }
    }
    if (brief.closed || brief.status === "Canceled" || brief.status === "Completed") {
        return {
            title: brief.status === "Canceled"
                ? "Commission canceled"
                : brief.closed
                    ? "Commission complete"
                    : "Delivery accepted",
            body: brief.status === "Canceled"
                ? "No further work can be reported."
                : brief.closed
                    ? "Delivery and settlement are complete. The accepted terms remain available for reference."
                    : "The commissioner accepted delivery. Financial settlement remains separate, and ordinary crafter actions are now locked.",
            tone: "blocked"
        };
    }
    if (!brief.isClaimed) {
        if (!state.capabilities.claimCapability) {
            return {
                title: "Open for one crafter",
                body: "This stable public link is view-only. Use the company's claim link to reserve the commission.",
                tone: "waiting"
            };
        }
        if (pending?.kind === "claim") {
            return {
                title: "A saved claim is ready to retry",
                body: "The participant secret and command ID were saved before the earlier request. Retrying replays that same atomic claim rather than creating another identity.",
                tone: "waiting",
                actions: [{
                    label: "Retry saved claim",
                    primary: true,
                    run: retryClaim
                }]
            };
        }
        return {
            title: "Claim this commission",
            body: "Verify one exact Lodestone character, provide a usable contact, and atomically accept the current terms.",
            actions: [{
                label: "Enter crafter details",
                primary: true,
                run: openClaimForm
            }]
        };
    }
    if (projection.kind !== "participant") {
        if (state.capabilities.recoveryCapability) {
            return {
                title: "Restore participant access",
                body: "This recovery link can reissue browser authority without changing the claim, assignment, progress, or public URL.",
                tone: "waiting",
                actions: [{
                    label: pending?.kind === "recover" ? "Retry recovery" : "Restore this browser",
                    primary: true,
                    run: recoverAccess
                }]
            };
        }
        return {
            title: "Commission assigned",
            body: "This public link remains view-only. The assigned crafter can use their saved browser access, or ask the commissioner for a fresh recovery link.",
            tone: "blocked"
        };
    }

    if (requiresTermsAcknowledgement(projection)) {
        return {
            title: `Accept updated terms v${brief.terms.version}`,
            body: "The commissioner accepted a payment-policy change. Review the current terms before work continues.",
            tone: "waiting",
            actions: [{
                label: "Accept current terms",
                primary: true,
                run: acknowledgeTerms
            }]
        };
    }
    if (brief.gates.identity === "Pending") {
        return {
            title: "Waiting for identity confirmation",
            body: "Lodestone proves that the character exists; the commissioner must still confirm that the submitted contact and character belong together.",
            tone: "waiting"
        };
    }
    const prework = resolveParticipantPreworkChoices(projection);
    if (prework.paymentPending || prework.materialsPending) {
        const bothPending = prework.paymentPending && prework.materialsPending;
        const paymentConfirmed = Boolean(projection.payment?.crafterReceived);
        const title = bothPending
            ? "Complete the remaining start requirements"
            : prework.paymentPending
                ? paymentConfirmed
                    ? "Waiting for commissioner confirmation"
                    : "Confirm advance payment"
                : prework.materialsReady
                    ? "Confirm the complete material bundle"
                    : "Waiting for company materials";
        const body = bothPending
            ? "Payment and complete material handoff can be confirmed in either order. Crafting begins when both are complete."
            : prework.paymentPending
                ? paymentConfirmed
                    ? "You confirmed receipt. Work unlocks when the commissioner records that the same advance payment was sent."
                    : `Confirm receipt of ${formatGil(brief.terms.payment.total)} against terms v${brief.terms.version}. Work unlocks only after both parties record the same exchange.`
                : prework.materialsReady
                    ? "The commissioner marked every promised quantity ready. Confirm receipt only after the complete bundle has been handed over."
                    : "The commissioner must mark the complete promised bundle ready before you can acknowledge receipt.";
        return {
            title,
            body,
            tone: "waiting",
            actions: prework.choices.map(createPreworkAction)
        };
    }
    if (!brief.clearedToWork) {
        return {
            title: "Work is not cleared",
            body: "The commission service has not satisfied every applicable pre-work gate. Work commands remain blocked.",
            tone: "blocked"
        };
    }
    if (brief.deliveryReadiness.isReady) {
        return {
            title: "Complete delivery is ready",
            body: "The commissioner can now accept delivery. If something changed, withdraw readiness with a reason and continue reporting progress.",
            actions: [{
                label: "Withdraw readiness",
                run: openWithdrawReadinessForm
            }]
        };
    }
    if (isCompleteAndReady(brief)) {
        return {
            title: "Everything is ready for delivery",
            body: "All requested outputs have complete and ready quantities. Declare the complete commission ready for commissioner acceptance.",
            actions: [{
                label: "Mark ready for delivery",
                primary: true,
                run: declareReadiness
            }]
        };
    }
    return {
        title: "Report item-level progress",
        body: "Work is cleared. Record completed and ready quantities directly against each requested output.",
        actions: [{
            label: "Focus progress",
            primary: true,
            run: () => byId("progressForm").scrollIntoView({ behavior: "smooth", block: "center" })
        }]
    };
}

function createPreworkAction(choice) {
    switch (choice) {
        case "confirm-advance-payment":
            return {
                label: `I received ${formatGil(state.projection.public.terms.payment.total)}`,
                primary: true,
                run: openPaymentReceiptForm
            };
        case "acknowledge-company-materials":
            return {
                label: "I received the complete material bundle",
                primary: true,
                run: acknowledgeMaterials
            };
        case "request-payment-schedule-change":
            return { label: "Request schedule change", run: openPaymentRequestForm };
        case "retract-advance-payment-confirmation":
            return { label: "Retract my confirmation", run: openPaymentRetractionForm };
        default:
            throw new Error(`Unknown pre-work choice: ${choice}`);
    }
}

function requiresTermsAcknowledgement(projection) {
    const accepted = Math.max(
        latestActivityRevision(projection, "PaymentPolicyChangeAccepted"),
        latestActivityRevision(projection, "TermsAmended"));
    const acknowledged = latestActivityRevision(projection, "TermsAcknowledged");
    return accepted > acknowledged;
}

function areCompanyMaterialsReady(projection) {
    return projection.companyMaterialsReadyForHandoff;
}

function latestActivityRevision(projection, kind) {
    return projection.activity
        .filter(item => item.kind === kind)
        .reduce((latest, item) => Math.max(latest, item.commissionRevision), 0);
}

function canUseActiveParticipantMutations(projection) {
    const brief = projection.public;
    return projection.kind === "participant" &&
        brief.viewState === "Published" &&
        !brief.closed &&
        brief.status !== "Canceled" &&
        brief.status !== "Completed";
}

function canReleaseBeforeWork(projection) {
    const brief = projection.public;
    return canUseActiveParticipantMutations(projection) &&
        !brief.clearedToWork &&
        brief.status !== "InProgress" &&
        brief.status !== "AwaitingDelivery" &&
        brief.gates.payment !== "Satisfied" &&
        brief.gates.companyMaterials !== "Satisfied" &&
        !areCompanyMaterialsReady(projection) &&
        brief.outputProgress.every(progress =>
            progress.completedQuantity === 0 &&
            progress.readyQuantity === 0 &&
            progress.acceptedQuantity === 0);
}

function isCompleteAndReady(brief) {
    if (brief.terms.outputs.length === 0) return false;
    const progressByLine = new Map(brief.outputProgress.map(item => [item.lineId, item]));
    return brief.terms.outputs.every(output => {
        const progress = progressByLine.get(output.lineId);
        return progress &&
            progress.completedQuantity >= output.requiredQuantity &&
            progress.readyQuantity >= output.requiredQuantity;
    });
}

function renderGates() {
    const projection = state.projection;
    const brief = projection.public;
    setText(
        "clearanceSummary",
        brief.clearedToWork ? "All prerequisites satisfied" : "Work commands remain locked");
    const target = byId("gates");
    target.replaceChildren();
    target.append(
        createGate("Identity", brief.gates.identity, identityGateDetail(brief)),
        createGate("Payment", brief.gates.payment, paymentGateDetail(projection)),
        createGate("Company materials", brief.gates.companyMaterials, materialGateDetail(brief))
    );
}

function createGate(label, gateState, detail) {
    const gate = element("div", `gate is-${gateState.replace(/([a-z])([A-Z])/g, "$1-$2").toLowerCase()}`);
    gate.append(
        element("span", null, label),
        element("strong", null, gateLabels[gateState]),
        element("small", null, detail)
    );
    return gate;
}

function identityGateDetail(brief) {
    if (brief.gates.identity === "Satisfied") {
        return "Character existence and contact ownership are confirmed.";
    }
    if (!brief.isClaimed) return "Satisfied after claim and commissioner identity review.";
    return "Lodestone existence is separate from commissioner ownership confirmation.";
}

function paymentGateDetail(projection) {
    const brief = projection.public;
    const schedule = formatPaymentSchedule(brief.terms.payment.schedule);
    if (brief.gates.payment === "NotRequired") {
        return `${schedule}; no advance payment gate applies.`;
    }
    if (brief.gates.payment === "Satisfied") {
        return "Commissioner and crafter both confirmed the same terms-version payment.";
    }
    const count = projection.payment
        ? Number(Boolean(projection.payment.commissionerSent)) +
            Number(Boolean(projection.payment.crafterReceived))
        : 0;
    return `${schedule}; ${count} of 2 party confirmations recorded.`;
}

function materialGateDetail(brief) {
    const count = brief.terms.materials.filter(item => item.responsibility === "Provided").length;
    if (brief.gates.companyMaterials === "NotRequired") {
        return "The company has no promised material bundle.";
    }
    if (brief.gates.companyMaterials === "Satisfied") {
        return `The complete ${count}-line promised bundle was acknowledged.`;
    }
    return "Every promised company-provided quantity must be received together.";
}

function renderOutputs() {
    const projection = state.projection;
    const brief = projection.public;
    const canEdit = canUseActiveParticipantMutations(projection) &&
        brief.clearedToWork &&
        !brief.deliveryReadiness.isReady;
    const progressByLine = new Map(brief.outputProgress.map(item => [item.lineId, item]));
    const target = byId("outputs");
    target.replaceChildren();

    for (const output of brief.terms.outputs) {
        const progress = progressByLine.get(output.lineId) ?? null;
        const row = element("div", "output-grid output-row");
        const identity = element("div", "output-identity");
        const percent = progress
            ? Math.min(100, progress.completedQuantity / output.requiredQuantity * 100)
            : 0;
        const meter = element("div", "progress-meter");
        const fill = element("span");
        fill.style.width = `${percent}%`;
        identity.append(
            element("strong", null, output.name),
            element("small", null, progress
                ? `Updated ${formatShortDate(progress.updatedAtUtc)}`
                : "No progress reported"),
            meter
        );
        meter.append(fill);

        const required = element(
            "span",
            "output-required",
            `${formatNumber(output.requiredQuantity)}${output.mustBeHq ? " HQ" : ""}`);
        const completed = renderProgressQuantity(
            "completed",
            output,
            progress?.completedQuantity ?? null,
            canEdit);
        const ready = renderProgressQuantity(
            "ready",
            output,
            progress?.readyQuantity ?? null,
            canEdit);
        row.append(identity, required, completed, ready);
        target.append(row);
    }

    byId("progressActions").hidden = !canEdit;
}

function renderProgressQuantity(kind, output, value, canEdit) {
    const wrapper = element("div", kind === "completed" ? "output-completed" : "output-ready");
    if (!canEdit) {
        wrapper.textContent = value == null ? "—" : formatNumber(value);
        return wrapper;
    }
    const label = element(
        "label",
        "sr-only",
        `${kind === "completed" ? "Completed" : "Ready"} ${output.name}`);
    const input = createInput("number", `${kind}-${output.lineId}`, {
        min: 0,
        max: output.requiredQuantity,
        value: value ?? 0,
        required: true
    });
    input.className = "quantity-input";
    input.dataset.lineId = output.lineId;
    input.dataset.itemId = String(output.itemId);
    input.dataset.kind = kind;
    wrapper.append(label, input);
    return wrapper;
}

function renderMaterials() {
    const projection = state.projection;
    const brief = projection.public;
    const provided = brief.terms.materials.filter(item => item.responsibility === "Provided");
    const crafter = brief.terms.materials.filter(item => item.responsibility === "Crafter");
    setText("materialTermsVersion", `Accepted terms v${brief.terms.version}`);
    setText(
        "companyMaterialSummary",
        provided.length
            ? "The full promised bundle must be acknowledged together."
            : "The company is not providing materials.");
    renderMaterialList("companyMaterials", provided, true);
    renderMaterialList("crafterMaterials", crafter, false);

    const acknowledge = byId("acknowledgeMaterials");
    acknowledge.hidden = !(canUseActiveParticipantMutations(projection) &&
        brief.gates.companyMaterials === "Pending" &&
        areCompanyMaterialsReady(projection));
    acknowledge.onclick = acknowledge.hidden ? null : acknowledgeMaterials;
}

function renderMaterialList(targetId, materials, companyProvides) {
    const target = byId(targetId);
    target.replaceChildren();
    if (!materials.length) {
        target.append(element(
            "p",
            "empty-row",
            companyProvides
                ? "No company-provided materials."
                : "No crafter-procured materials."));
        return;
    }

    for (const material of materials) {
        const row = element("div", "material-row");
        const identity = element("div", "material-identity");
        identity.append(
            element("strong", null, material.name),
            element(
                "small",
                null,
                `${material.requiresHq ? "HQ required" : "NQ accepted"} · ${formatGil(material.unitCost)} each`)
        );
        row.append(
            identity,
            element("span", "material-quantity", `×${formatNumber(material.quantity)}`),
            element(
                "strong",
                "material-total",
                companyProvides ? `${formatGil(material.totalCost)} value` : formatGil(material.totalCost))
        );
        target.append(row);
    }
}

function renderTerms() {
    const brief = state.projection.public;
    const terms = brief.terms;
    setText("termsVersion", `v${terms.version}`);
    const target = byId("terms");
    target.replaceChildren();
    appendTextRow(target, "Payment", formatPaymentSchedule(terms.payment.schedule));
    appendTextRow(target, "Material reimbursement", formatGil(terms.payment.materialReimbursement));
    if (terms.payment.materialAdjustment > 0) {
        appendTextRow(target, "Adjustment", formatGil(terms.payment.materialAdjustment));
    }
    if (terms.payment.craftLabor > 0) {
        appendTextRow(target, "Craft labor", formatGil(terms.payment.craftLabor));
    }
    if (terms.payment.craftSynthCount > 0 && terms.payment.gilPerSynth > 0) {
        appendTextRow(
            target,
            "Labor basis",
            `${formatNumber(terms.payment.craftSynthCount)} synths x ${formatGil(terms.payment.gilPerSynth)}`);
    }
    appendTextRow(target, "Total", formatGil(terms.payment.total));
    appendTextRow(target, "Settlement", formatWords(brief.settlementState));
    if (state.projection.settlementPayment &&
        (brief.settlementState === "Pending" ||
         state.projection.settlementPayment.confirmationCount > 0)) {
        const settlement = state.projection.settlementPayment;
        appendTextRow(
            target,
            "Final payment confirmations",
            `${Number(Boolean(settlement.commissionerSent)) + Number(Boolean(settlement.crafterReceived))} of 2`);
    }
    appendTextRow(target, "Reference", brief.reference);

    const detail = byId("termsDetail");
    detail.replaceChildren();
    detail.append(
        element("span", null, `Delivery: ${formatDeliveryInstructions(terms.deliveryInstructions)}`),
        element("span", null, `Payment contract: ${terms.payment.contractLabel}`),
        element(
            "span",
            null,
            `Evidence: ${terms.pricingEvidence.costBasis}; ${terms.pricingEvidence.marketScope}; ${terms.pricingEvidence.location}; captured ${formatDate(terms.pricingEvidence.capturedAtUtc)}.`)
    );
    if (terms.payment.customTerms) {
        detail.append(element("span", null, `Custom payment terms: ${terms.payment.customTerms}`));
    }
    if (terms.contactInstructions) {
        detail.append(element("span", null, `Contact: ${terms.contactInstructions}`));
    }

    const paymentActions = byId("paymentActions");
    paymentActions.replaceChildren();
    const canRequestWhileIdentityWaits = canUseActiveParticipantMutations(state.projection) &&
        brief.gates.identity === "Pending" &&
        brief.gates.payment === "Pending" &&
        !brief.clearedToWork;
    paymentActions.hidden = !canRequestWhileIdentityWaits;
    if (canRequestWhileIdentityWaits) {
        paymentActions.append(createButton(
            "Request schedule change",
            openPaymentRequestForm));
    }
}

function renderAccess() {
    const projection = state.projection;
    const accessNote = byId("accessNote");
    const actions = byId("accessActions");
    accessNote.replaceChildren();
    actions.replaceChildren();

    if (projection.kind === "participant") {
        const canMutate = canUseActiveParticipantMutations(projection);
        setText("accessState", canMutate ? "Can update order" : "Reference only");
        accessNote.append(
            element(
                "strong",
                null,
                canMutate
                    ? "This browser can update the commission."
                    : "This participant access is retained for reference."),
            element(
                "small",
                null,
                canMutate
                    ? "The public URL is view-only. A commissioner-issued recovery link can reissue this access without replacing the commission or its progress."
                    : "Canceled and fulfilled commissions do not accept ordinary participant mutations.")
        );
        if (projection.provisionalCrafter) {
            const crafter = projection.provisionalCrafter;
            accessNote.append(element(
                "small",
                null,
                `${crafter.characterName} · ${crafter.homeWorld} · ${crafter.contactMethod}: ${crafter.contactValue}`));
        }
        if (canReleaseBeforeWork(projection)) {
            actions.append(createButton(
                "Release commission",
                openReleaseForm,
                "button danger"));
        }
        return;
    }

    setText("accessState", "View only");
    accessNote.append(
        element("strong", null, "This stable public link cannot mutate the commission."),
        element(
            "small",
            null,
            state.projection.public.isClaimed
                ? "Use saved participant access, or ask the commissioner for a fresh recovery link after browser-data loss or a device change."
                : "A separate claim-capable link is required to reserve the one active claim slot.")
    );
}

function renderActivity() {
    const target = byId("activity");
    target.replaceChildren();
    if (state.projection.kind !== "participant") {
        target.append(element(
            "p",
            "empty-row",
            "Operational activity is available only to the authorized participant and company operators."));
        return;
    }
    if (!state.projection.activity.length) {
        target.append(element("p", "empty-row", "No operational activity has been recorded."));
        return;
    }

    for (const item of [...state.projection.activity].sort(
        (left, right) => right.commissionRevision - left.commissionRevision)) {
        const event = element("div", "event");
        const detail = element("div");
        detail.append(
            element("strong", null, activityLabels[item.kind] ?? formatWords(item.kind)),
            element(
                "span",
                null,
                `${item.actorDisplayName ?? formatWords(item.actorKind)} · terms v${item.termsVersion}`)
        );
        if (item.comment) detail.append(element("p", null, item.comment));
        event.append(element("time", null, formatShortDate(item.createdAtUtc)), detail);
        target.append(event);
    }
}

function renderCommentForm() {
    const allowed = canUseActiveParticipantMutations(state.projection);
    const form = byId("commentForm");
    form.hidden = !allowed;
    form.onsubmit = allowed ? submitComment : null;
}

function formatPaymentSchedule(value) {
    if (value === "OnDelivery") return "On delivery";
    return formatWords(value);
}

function formatDeliveryInstructions(value) {
    return value || "No delivery instructions were published.";
}

function formatWords(value) {
    return value.replace(/([a-z])([A-Z])/g, "$1 $2");
}

function requiredUserText(value, label) {
    const text = value.trim();
    if (!text) {
        throw new CommissionClientError(`${label} is required.`, "invalid-input");
    }
    return text;
}

function showActionForm(form) {
    const target = byId("nextStepForm");
    target.replaceChildren(form);
    target.hidden = false;
    target.scrollIntoView({ behavior: "smooth", block: "center" });
}

function addFormFooter(form, submitLabel, onCancel = closeActionForm) {
    const actions = element("div", "inline-actions");
    const submit = element("button", "button primary", submitLabel);
    submit.type = "submit";
    actions.append(
        createButton("Cancel", onCancel),
        submit
    );
    form.append(actions);
}

function closeActionForm() {
    byId("nextStepForm").replaceChildren();
    byId("nextStepForm").hidden = true;
}

function openClaimForm() {
    state.selectedLodestone = null;
    const form = element("form", "action-form");
    const grid = element("div", "form-grid");
    const character = createInput("text", "characterName", {
        required: true,
        maxLength: 64,
        autocomplete: "off",
        placeholder: "First Last"
    });
    const world = createInput("text", "homeWorld", {
        required: true,
        maxLength: 32,
        autocomplete: "off",
        placeholder: "Behemoth"
    });
    const contactMethod = createSelect("contactMethod", [
        ["Discord", "Discord"],
        ["Email", "Email"],
        ["Other", "Other usable contact"]
    ]);
    const contactValue = createInput("text", "contactValue", {
        required: true,
        maxLength: 240,
        autocomplete: "off",
        placeholder: "How the commissioner can reach you"
    });
    grid.append(
        createField("In-game character", character),
        createField("Home world", world),
        createField("Contact method", contactMethod),
        createField("Contact details", contactValue)
    );

    const searchActions = element("div", "inline-actions");
    const search = createButton("Find on Lodestone", async () => {
        await searchLodestone(character.value, world.value, results, preview, error);
    }, "button primary");
    searchActions.append(search);
    const results = element("div", "lodestone-results");
    const preview = element("div");
    const error = element("p", "form-error");
    error.hidden = true;
    form.append(
        grid,
        element(
            "p",
            "form-help",
            "Lodestone confirms that the selected character exists; the commissioner separately confirms that the contact belongs to that character."),
        searchActions,
        error,
        results,
        preview
    );
    addFormFooter(form, `Claim terms v${state.projection.public.terms.version}`);
    form.addEventListener("submit", async event => {
        event.preventDefault();
        if (!state.selectedLodestone) {
            error.textContent = "Select and verify one exact Lodestone character before claiming.";
            error.hidden = false;
            return;
        }
        try {
            await submitClaim({
                contactMethod: contactMethod.value,
                contactValue: contactValue.value.trim()
            });
        } catch (caught) {
            showNotice("Claim unavailable", caught.message);
        }
    });
    showActionForm(form);
}

async function searchLodestone(characterName, worldName, results, preview, error) {
    error.hidden = true;
    results.replaceChildren(element("p", "form-help", "Searching Lodestone…"));
    preview.replaceChildren();
    state.selectedLodestone = null;
    try {
        const candidates = await state.lodestone.search(characterName, worldName);
        results.replaceChildren();
        if (!candidates.length) {
            results.append(element("p", "form-help", "No matching Lodestone characters were found."));
            return;
        }
        for (const candidate of candidates) {
            const button = element("button", "lodestone-result");
            button.type = "button";
            button.append(
                element("strong", null, candidate.displayName),
                element(
                    "span",
                    null,
                    `${candidate.worldName}${candidate.dataCenter ? ` · ${candidate.dataCenter}` : ""}`)
            );
            button.addEventListener("click", async () => {
                await selectLodestoneCandidate(candidate, preview, error);
            });
            results.append(button);
        }
    } catch (caught) {
        error.textContent = caught.message;
        error.hidden = false;
        results.replaceChildren();
    }
}

async function selectLodestoneCandidate(candidate, target, error) {
    error.hidden = true;
    target.replaceChildren(element("p", "form-help", "Loading exact character details…"));
    try {
        const preview = await state.lodestone.preview(candidate.lodestoneCharacterId);
        if (preview.displayName !== candidate.displayName ||
            preview.worldName.toLowerCase() !== candidate.worldName.toLowerCase()) {
            throw new CommissionClientError(
                "The Lodestone preview no longer matches the selected search result.",
                "lodestone-candidate-changed");
        }
        state.selectedLodestone = preview;
        const card = element("div", "lodestone-preview");
        if (preview.avatarUrl) {
            const avatar = document.createElement("img");
            avatar.src = preview.avatarUrl;
            avatar.alt = "";
            avatar.referrerPolicy = "no-referrer";
            card.append(avatar);
        } else {
            card.append(element("span", null, "✓"));
        }
        const identity = element("div");
        identity.append(
            element("strong", null, `${preview.displayName} · ${preview.worldName}`),
            element(
                "span",
                null,
                `${preview.freeCompanyName ?? "No Free Company shown"} · Verified ${formatShortDate(preview.retrievedAtUtc)}`)
        );
        card.append(identity);
        target.replaceChildren(card);
    } catch (caught) {
        state.selectedLodestone = null;
        target.replaceChildren();
        error.textContent = caught.message;
        error.hidden = false;
    }
}

async function submitClaim(contact) {
    const selected = state.selectedLodestone;
    const contactValue = requiredUserText(contact.contactValue, "Contact details");
    const payload = {
        termsVersion: state.projection.public.terms.version,
        provisionalCrafter: {
            provisionalCrafterId: crypto.randomUUID(),
            characterName: selected.displayName,
            homeWorld: selected.worldName,
            contactMethod: contact.contactMethod,
            contactValue,
            discordUserId: null,
            discordDisplayNameSnapshot: null,
            lodestoneCharacterId: selected.lodestoneCharacterId,
            lodestoneProfileUrl: selected.lodestoneProfileUrl,
            submittedAtUtc: new Date().toISOString()
        },
        existingCrafterId: null
    };
    const access = state.store.beginAuthorityExchange("claim", payload);
    state.access = access;
    await sendAuthorityExchange(
        "claim",
        access,
        state.capabilities.claimCapability,
        "Commission claimed. Participant access is saved in this browser.");
}

async function retryClaim() {
    const access = state.store.load();
    if (!access?.pending || access.pending.kind !== "claim") {
        throw new CommissionClientError("No saved claim command is available.", "missing-pending-claim");
    }
    await sendAuthorityExchange(
        "claim",
        access,
        state.capabilities.claimCapability,
        "Commission claimed. Participant access is saved in this browser.");
}

async function recoverAccess() {
    if (!state.capabilities.recoveryGrantId) {
        throw new CommissionClientError(
            "The recovery link does not identify a one-time recovery grant.",
            "missing-recovery-grant");
    }
    const access = state.store.beginAuthorityExchange("recover", {
        recoveryGrantId: state.capabilities.recoveryGrantId
    });
    state.access = access;
    await sendAuthorityExchange(
        "redeem-participant-recovery",
        access,
        state.capabilities.recoveryCapability,
        "Participant access restored in this browser.");
}

async function sendAuthorityExchange(command, access, authority, successMessage) {
    if (!authority) {
        showNotice(
            "Authority missing",
            "The URL fragment no longer contains the required claim or recovery capability. Ask the commissioner for a fresh link.");
        return;
    }
    setBusy(true);
    clearNotice();
    let applied = false;
    try {
        const authorization = createCommandAuthorization(
            state.projection,
            null,
            command === "claim"
                ? { claimCapability: authority }
                : { recoveryCapability: authority },
            access.pending.commandId);
        await state.client.command(
            command,
            {
                ...access.pending.payload,
                newParticipantCredential: access.participantSecret
            },
            authorization);
        applied = true;
        state.store.completeAuthorityExchange(access);
        clearCapabilityFragment();
        state.capabilities = {
            claimCapability: null,
            recoveryCapability: null,
            recoveryGrantId: null
        };
        await reloadProjection(successMessage);
    } catch (caught) {
        showNotice(
            applied ? "The command was accepted but the brief did not refresh" : "The command did not complete",
            applied
                ? `${caught.message} The saved participant secret remains available for recovery.`
                : `${caught.message} The saved secret and command ID are still available for an exact retry.`);
    } finally {
        setBusy(false);
    }
}

function openPaymentRequestForm() {
    const form = element("form", "action-form");
    const schedule = createSelect("requestedSchedule", [
        ["Advance", "Advance"],
        ["OnDelivery", "On delivery"],
        ["Custom", "Custom timing"]
    ]);
    schedule.value = state.projection.public.terms.payment.schedule;
    const customTerms = createInput("text", "requestedCustomTerms", {
        maxLength: 500,
        placeholder: "Describe timing only; the agreed total does not change here"
    });
    const customField = createField("Requested custom timing", customTerms);
    customField.hidden = schedule.value !== "Custom";
    schedule.addEventListener("change", () => {
        customField.hidden = schedule.value !== "Custom";
        customTerms.required = schedule.value === "Custom";
    });
    const reason = createTextarea("reason", {
        required: true,
        maxLength: 1000,
        placeholder: "Why would a different payment schedule help?"
    });
    form.append(
        element(
            "p",
            "form-help",
            `The accepted total remains ${formatGil(state.projection.public.terms.payment.total)}. This request proposes timing only and does not alter authoritative pricing.`),
        createField("Requested schedule", schedule),
        customField,
        createField("Reason", reason)
    );
    addFormFooter(form, "Send request");
    form.addEventListener("submit", async event => {
        event.preventDefault();
        try {
            await runParticipantCommand(
                "request-payment-policy-change",
                {
                    requestedSchedule: paymentScheduleValue(schedule.value),
                    requestedCustomTerms: schedule.value === "Custom"
                        ? requiredUserText(customTerms.value, "Custom timing")
                        : null,
                    reason: requiredUserText(reason.value, "Reason")
                },
                "Payment schedule request sent.");
        } catch (caught) {
            showNotice("Payment request unavailable", caught.message);
        }
    });
    showActionForm(form);
}

function openPaymentReceiptForm() {
    const brief = state.projection.public;
    const form = element("form", "action-form");
    const note = createTextarea("note", {
        required: true,
        maxLength: 500,
        placeholder: "Where or how was the payment received?"
    });
    form.append(
        element(
            "p",
            "form-help",
            `Confirm ${formatGil(brief.terms.payment.total)} received for terms v${brief.terms.version}. This is your attestation, not automated in-game evidence.`),
        createField("Receipt note", note)
    );
    addFormFooter(form, "Confirm receipt");
    form.addEventListener("submit", async event => {
        event.preventDefault();
        try {
            await runParticipantCommand(
                "confirm-payment-received",
                {
                    termsVersion: brief.terms.version,
                    note: requiredUserText(note.value, "Receipt note")
                },
                "Payment receipt confirmed.");
        } catch (caught) {
            showNotice("Payment confirmation unavailable", caught.message);
        }
    });
    showActionForm(form);
}

function openPaymentRetractionForm() {
    openReasonForm(
        "Retract payment confirmation",
        "Explain what was incorrect. Work remains locked until both parties confirm the corrected exchange.",
        "Retract confirmation",
        "retract-payment",
        "Payment confirmation retracted.");
}

function openSettlementReceiptForm() {
    const brief = state.projection.public;
    const form = element("form", "action-form");
    const note = createTextarea("note", {
        required: true,
        maxLength: 500,
        placeholder: "Where or how was the final payment received?"
    });
    form.append(
        element(
            "p",
            "form-help",
            `Confirm ${formatGil(brief.terms.payment.total)} received for terms v${brief.terms.version}. This is your attestation, not automated in-game evidence.`),
        createField("Receipt note", note)
    );
    addFormFooter(form, "Confirm final payment");
    form.addEventListener("submit", async event => {
        event.preventDefault();
        try {
            await runParticipantCommand(
                "confirm-settlement-received",
                {
                    termsVersion: brief.terms.version,
                    note: requiredUserText(note.value, "Receipt note")
                },
                "Final payment receipt confirmed.");
        } catch (caught) {
            showNotice("Settlement confirmation unavailable", caught.message);
        }
    });
    showActionForm(form);
}

function openSettlementRetractionForm() {
    openReasonForm(
        "Retract final-payment confirmation",
        "Explain what was incorrect. The commission returns to settlement pending until both parties confirm the corrected exchange.",
        "Retract confirmation",
        "retract-settlement",
        "Final-payment confirmation retracted.");
}

function paymentScheduleValue(schedule) {
    return { Advance: 0, OnDelivery: 1, Custom: 2 }[schedule];
}

function openReleaseForm() {
    openReasonForm(
        "Release commission",
        "Explain why you are releasing the claim. The slot may reopen, but this event remains in commission history.",
        "Release claim",
        "release-claim",
        "Claim released.");
}

function openWithdrawReadinessForm() {
    openReasonForm(
        "Withdraw delivery readiness",
        "Progress remains intact; explain what needs more work.",
        "Withdraw readiness",
        "withdraw-readiness",
        "Delivery readiness withdrawn.");
}

function openReasonForm(title, help, submitLabel, command, successMessage) {
    const form = element("form", "action-form");
    const reason = createTextarea("reason", {
        required: true,
        maxLength: 1000,
        placeholder: help
    });
    form.append(element("strong", null, title), element("p", "form-help", help), createField("Reason", reason));
    addFormFooter(form, submitLabel);
    form.addEventListener("submit", async event => {
        event.preventDefault();
        try {
            await runParticipantCommand(
                command,
                { reason: requiredUserText(reason.value, "Reason") },
                successMessage);
        } catch (caught) {
            showNotice("Action unavailable", caught.message);
        }
    });
    showActionForm(form);
}

async function acknowledgeTerms() {
    await runParticipantCommand(
        "acknowledge-terms",
        { termsVersion: state.projection.public.terms.version },
        `Terms v${state.projection.public.terms.version} accepted.`);
}

async function acknowledgeMaterials() {
    const quantities = state.projection.public.terms.materials
        .filter(item => item.responsibility === "Provided")
        .map(item => ({
            lineId: item.lineId,
            itemId: item.itemId,
            quantity: item.quantity
        }));
    if (!quantities.length) {
        showNotice(
            "No promised bundle",
            "The accepted terms contain no company-provided material quantities.");
        return;
    }
    await runParticipantCommand(
        "acknowledge-company-materials",
        { quantities },
        "Complete company material bundle acknowledged.");
}

async function submitProgress(event) {
    event.preventDefault();
    const brief = state.projection.public;
    const outputs = [];
    for (const output of brief.terms.outputs) {
        const completed = Number(document.querySelector(`[name="completed-${output.lineId}"]`)?.value);
        const ready = Number(document.querySelector(`[name="ready-${output.lineId}"]`)?.value);
        if (!Number.isSafeInteger(completed) ||
            !Number.isSafeInteger(ready) ||
            completed < 0 ||
            completed > output.requiredQuantity ||
            ready < 0 ||
            ready > completed) {
            showNotice(
                "Progress is invalid",
                `${output.name}: completed must be between 0 and ${output.requiredQuantity}, and ready cannot exceed completed.`);
            return;
        }
        outputs.push({
            lineId: output.lineId,
            itemId: output.itemId,
            completedQuantity: completed,
            readyQuantity: ready
        });
    }
    await runParticipantCommand(
        "report-progress",
        { outputs, comment: null },
        "Item-level progress saved.");
}

async function submitComment(event) {
    event.preventDefault();
    const input = byId("commentText");
    const comment = input.value.trim();
    if (!comment) return;
    await runParticipantCommand("add-comment", { comment }, "Operational update posted.");
    input.value = "";
}

async function declareReadiness() {
    await runParticipantCommand(
        "declare-readiness",
        { comment: null },
        "Complete delivery marked ready.");
}

async function runParticipantCommand(command, payload, successMessage) {
    if (!canUseActiveParticipantMutations(state.projection) ||
        !state.access?.participantSecret) {
        showNotice(
            "Participant action unavailable",
            "This browser lacks active participant authority, or the commission is no longer open to ordinary participant changes.");
        return;
    }
    if (command === "release-claim" && !canReleaseBeforeWork(state.projection)) {
        showNotice(
            "Release requires commissioner reconciliation",
            "Ordinary release is available only before payment, company-material receipt, work clearance, or progress begins.");
        return;
    }
    setBusy(true);
    clearNotice();
    let applied = false;
    try {
        await state.client.command(
            command,
            payload,
            createCommandAuthorization(state.projection, state.access));
        applied = true;
        closeActionForm();
        await reloadProjection(successMessage);
    } catch (caught) {
        showNotice(
            applied ? "The commission changed but the brief did not refresh" : "The commission was not changed",
            caught.message);
    } finally {
        setBusy(false);
    }
}

byId("progressForm").addEventListener("submit", submitProgress);
window.addEventListener("pagehide", () => state.projectionStream?.stop(), { once: true });
load().catch(showFatal);
