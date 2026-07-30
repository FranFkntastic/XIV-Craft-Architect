const PROTOCOL_VERSION = 1;
const ACCESS_STORAGE_PREFIX = "craftArchitect.companyCommission.participant.v1.";
const PUBLIC_ID_PATTERN = /^[A-Za-z0-9_-]{8,128}$/;
const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const CAPABILITY_PATTERN = /^[A-Za-z0-9_-]{32,512}$/;

const enumMaps = {
    viewState: ["Draft", "Published", "Revoked"],
    paymentSchedule: ["Advance", "OnDelivery", "Custom"],
    clearance: ["NotRequired", "Pending", "Satisfied"],
    settlement: ["NotDue", "Pending", "Satisfied"],
    materialResponsibility: ["Crafter", "Provided"],
    status: ["Draft", "ReadyToAssign", "Assigned", "InProgress", "AwaitingDelivery", "Completed", "Canceled"],
    actorKind: ["Commissioner", "Crafter", "System", "Migration"],
    sourceSurface: ["TradeArchitect", "PublicBrief", "Discord", "HostedMigration", "System"],
    activityKind: [
        "CommissionOpened",
        "ClaimAccepted",
        "ClaimRejected",
        "ClaimReleased",
        "ClaimRecovered",
        "ProvisionalIdentitySubmitted",
        "ProvisionalIdentityConfirmed",
        "ProvisionalIdentityRejected",
        "PaymentPolicyChangeRequested",
        "PaymentPolicyChangeAccepted",
        "PaymentPolicyChangeRefused",
        "TermsAcknowledged",
        "PaymentClearanceRecorded",
        "CompanyMaterialsReady",
        "CompanyMaterialsReceived",
        "WorkClearanceAchieved",
        "ProgressReported",
        "CommentAdded",
        "DeliveryReadinessDeclared",
        "DeliveryReadinessWithdrawn",
        "DeliveryReturnedToWork",
        "DeliveryAccepted",
        "SettlementRecorded",
        "CommissionCanceled",
        "CommissionClosed",
        "CommissionPublicationRevoked",
        "ParticipantRecoveryIssued",
        "ParticipantRecoveryRedeemed",
        "MigratedFromTradeOrder",
        "MigratedTradeOrderHistory"
    ]
};

export class CommissionClientError extends Error {
    constructor(message, code = "unknown", status = 0) {
        super(message);
        this.name = "CommissionClientError";
        this.code = code;
        this.status = status;
    }
}

export class CommissionBriefApiClient {
    constructor(publicId, fetchImpl = window.fetch.bind(window)) {
        if (!PUBLIC_ID_PATTERN.test(publicId)) {
            throw new CommissionClientError("This commission link has an invalid public identifier.", "invalid-public-id");
        }

        this.publicId = publicId;
        this.fetch = fetchImpl;
        this.briefPath = `/api/xivdata/commission-briefs/${encodeURIComponent(publicId)}`;
    }

    async load(participantSecret = null) {
        const headers = { "Accept": "application/json" };
        if (participantSecret) {
            headers["X-Commission-Participant"] = participantSecret;
        }

        const response = await this.fetch(this.briefPath, {
            method: "GET",
            headers,
            cache: "no-store"
        });
        return adaptBriefProjection(await adaptResponse(response));
    }

    async command(command, payload, authorization) {
        const body = {
            protocolVersion: PROTOCOL_VERSION,
            publicBriefId: this.publicId,
            expectedProjectionRevision: authorization.expectedProjectionRevision,
            commandId: authorization.commandId,
            participantCapability: authorization.participantSecret ?? null,
            claimCapability: authorization.claimCapability ?? null,
            recoveryCapability: authorization.recoveryCapability ?? null,
            command: payload
        };

        const response = await this.fetch(
            `${this.briefPath}/commands/${encodeURIComponent(command)}`,
            {
                method: "POST",
                headers: {
                    "Accept": "application/json",
                    "Content-Type": "application/json"
                },
                body: JSON.stringify(body)
            });
        return adaptResponse(response);
    }
}

export class LodestoneExistenceClient {
    constructor(fetchImpl = window.fetch.bind(window)) {
        this.fetch = fetchImpl;
    }

    async search(characterName, worldName) {
        const query = new URLSearchParams({
            name: requiredText(characterName, "Character name"),
            world: requiredText(worldName, "Home world")
        });
        const result = await adaptResponse(await this.fetch(
            `/api/lodestone/crafters/search?${query}`,
            {
                headers: { "Accept": "application/json" },
                cache: "no-store"
            }));
        if (!result.succeeded || !Array.isArray(result.value)) {
            throw new CommissionClientError(
                result.errorMessage || "Lodestone could not verify that character.",
                "lodestone-search-failed");
        }

        return result.value.map(candidate => ({
            lodestoneCharacterId: requiredText(candidate.lodestoneCharacterId, "Lodestone character ID"),
            displayName: requiredText(candidate.displayName, "Character name"),
            worldName: requiredText(candidate.worldName, "Home world"),
            dataCenter: optionalText(candidate.dataCenter),
            lodestoneProfileUrl: requiredHttpsUrl(candidate.lodestoneProfileUrl, "Lodestone profile URL")
        }));
    }

    async preview(characterId) {
        const id = requiredText(characterId, "Lodestone character ID");
        const result = await adaptResponse(await this.fetch(
            `/api/lodestone/crafters/${encodeURIComponent(id)}/preview`,
            {
                headers: { "Accept": "application/json" },
                cache: "no-store"
            }));
        if (!result.succeeded || !result.value) {
            throw new CommissionClientError(
                result.errorMessage || "Lodestone could not load that character.",
                "lodestone-preview-failed");
        }

        return {
            lodestoneCharacterId: requiredText(result.value.lodestoneCharacterId, "Lodestone character ID"),
            displayName: requiredText(result.value.displayName, "Character name"),
            worldName: requiredText(result.value.worldName, "Home world"),
            dataCenter: optionalText(result.value.dataCenter),
            lodestoneProfileUrl: requiredHttpsUrl(result.value.lodestoneProfileUrl, "Lodestone profile URL"),
            avatarUrl: optionalHttpsUrl(result.value.avatarUrl),
            freeCompanyName: optionalText(result.value.freeCompanyName),
            retrievedAtUtc: requiredDate(result.value.retrievedAtUtc, "Lodestone retrieval time")
        };
    }
}

export class ParticipantAccessStore {
    constructor(publicId, storage = window.localStorage) {
        this.publicId = publicId;
        this.storage = storage;
        this.key = `${ACCESS_STORAGE_PREFIX}${publicId}`;
    }

    load() {
        let serialized;
        try {
            serialized = this.storage.getItem(this.key);
        } catch {
            throw new CommissionClientError(
                "Browser storage is unavailable. Claim and recovery are blocked because a new participant secret cannot be saved before the command.",
                "storage-unavailable");
        }

        if (!serialized) {
            return null;
        }

        try {
            const value = JSON.parse(serialized);
            if (value.version !== 1 ||
                value.publicId !== this.publicId ||
                typeof value.participantSecret !== "string" ||
                value.participantSecret.length < 40) {
                throw new Error("Invalid participant access record.");
            }
            return value;
        } catch {
            throw new CommissionClientError(
                "Saved participant access is damaged. Ask the commissioner for a fresh recovery link.",
                "invalid-saved-access");
        }
    }

    beginAuthorityExchange(kind, payload) {
        const existing = this.load();
        if (existing?.pending?.kind === kind) {
            return existing;
        }

        const access = {
            version: 1,
            publicId: this.publicId,
            participantSecret: createSecret(),
            savedAtUtc: new Date().toISOString(),
            pending: {
                kind,
                commandId: crypto.randomUUID(),
                payload,
                createdAtUtc: new Date().toISOString()
            }
        };
        this.save(access);
        return access;
    }

    completeAuthorityExchange(access) {
        this.save({
            ...access,
            savedAtUtc: new Date().toISOString(),
            pending: null
        });
    }

    save(access) {
        try {
            this.storage.setItem(this.key, JSON.stringify(access));
        } catch {
            throw new CommissionClientError(
                "Browser storage could not save participant access. No command was sent.",
                "storage-write-failed");
        }
    }

    discard() {
        try {
            this.storage.removeItem(this.key);
        } catch {
            throw new CommissionClientError(
                "Damaged participant access could not be cleared from browser storage.",
                "storage-write-failed");
        }
    }
}

export function readCapabilityFragment(location = window.location) {
    const fragment = new URLSearchParams(location.hash.replace(/^#/, ""));
    const claimCapability = optionalCapability(fragment.get("claim"), "Claim capability");
    const recoveryAuthority = optionalText(fragment.get("recover"));
    if (claimCapability && recoveryAuthority) {
        throw new CommissionClientError(
            "This link contains both claim and recovery authority. Ask the commissioner for a fresh link.",
            "ambiguous-authority");
    }
    if (!recoveryAuthority) {
        return {
            claimCapability,
            recoveryCapability: null,
            recoveryGrantId: null
        };
    }

    const separator = recoveryAuthority.indexOf(".");
    if (separator <= 0 || separator === recoveryAuthority.length - 1) {
        throw new CommissionClientError(
            "This recovery link is malformed. Ask the commissioner for a fresh link.",
            "invalid-recovery-authority");
    }
    return {
        claimCapability: null,
        recoveryGrantId: requiredGuid(
            recoveryAuthority.slice(0, separator),
            "Recovery grant ID"),
        recoveryCapability: optionalCapability(
            recoveryAuthority.slice(separator + 1),
            "Recovery capability")
    };
}

export function clearCapabilityFragment(history = window.history, location = window.location) {
    history.replaceState(null, "", `${location.pathname}${location.search}`);
}

export function createCommandAuthorization(projection, access, capability = {}, commandId = crypto.randomUUID()) {
    return {
        expectedProjectionRevision: projection.public.projectionRevision,
        commandId,
        participantSecret: access?.participantSecret ?? null,
        claimCapability: capability.claimCapability ?? null,
        recoveryCapability: capability.recoveryCapability ?? null
    };
}

export function adaptBriefProjection(payload) {
    const isParticipant = Boolean(payload?.public);
    const publicBrief = adaptPublicBrief(isParticipant ? payload.public : payload);
    if (!isParticipant) {
        return {
            kind: "anonymous",
            public: publicBrief,
            provisionalCrafter: null,
            participantCapabilityRevision: null,
            activity: []
        };
    }

    return {
        kind: "participant",
        public: publicBrief,
        provisionalCrafter: payload.provisionalCrafter
            ? adaptProvisionalCrafter(payload.provisionalCrafter)
            : null,
        participantCapabilityRevision: requiredInteger(
            payload.participantCapabilityRevision,
            "Participant capability revision",
            0),
        activity: requiredArray(payload.activity, "Participant activity").map(adaptParticipantActivity)
    };
}

async function adaptResponse(response) {
    let payload = null;
    try {
        payload = await response.json();
    } catch {
        if (response.ok) {
            throw new CommissionClientError(
                "The commission service returned an empty or invalid response.",
                "invalid-response",
                response.status);
        }
    }

    if (response.ok) {
        return payload;
    }

    const code = optionalText(payload?.errorCode ?? payload?.code) ?? statusCode(response.status);
    const message = optionalText(payload?.errorMessage ?? payload?.message) ?? statusMessage(response.status);
    throw new CommissionClientError(message, code, response.status);
}

function adaptPublicBrief(source) {
    const brief = requiredObject(source, "Public commission brief");
    const terms = adaptPublicTerms(requiredObject(brief.terms, "Public commission terms"));
    const progress = requiredArray(brief.outputProgress, "Public output progress").map(adaptPublicProgress);
    const termLines = new Map(terms.outputs.map(output => [output.lineId, output]));
    const progressLines = new Set(progress.map(item => item.lineId));
    if (termLines.size !== terms.outputs.length ||
        progressLines.size !== progress.length ||
        progress.some(item => {
            const term = termLines.get(item.lineId);
            return !term ||
                term.itemId !== item.itemId ||
                term.requiredQuantity !== item.requiredQuantity;
        })) {
        throw new CommissionClientError(
            "Output progress does not match the accepted commission terms.",
            "invalid-output-progress");
    }

    return {
        publicBriefId: requiredText(brief.publicBriefId, "Public brief ID"),
        commissionId: requiredGuid(brief.commissionId, "Commission ID"),
        title: requiredText(brief.title, "Commission title"),
        companyDisplayName: requiredText(brief.companyDisplayName, "Company display name"),
        reference: requiredText(brief.reference, "Commission reference"),
        viewState: requiredEnum(brief.viewState, enumMaps.viewState, "Public view state"),
        terms,
        status: requiredEnum(brief.status, enumMaps.status, "Commission status"),
        gates: adaptPublicGates(requiredObject(brief.gates, "Public pre-work gates")),
        clearedToWork: requiredBoolean(brief.clearedToWork, "Work clearance"),
        isClaimed: requiredBoolean(brief.isClaimed, "Claim state"),
        outputProgress: progress,
        deliveryReadiness: adaptPublicReadiness(
            requiredObject(brief.deliveryReadiness, "Public delivery readiness")),
        settlementState: requiredEnum(brief.settlementState, enumMaps.settlement, "Settlement state"),
        closed: requiredBoolean(brief.closed, "Closed state"),
        projectionRevision: requiredInteger(brief.projectionRevision, "Projection revision", 0)
    };
}

function adaptPublicTerms(terms) {
    const outputs = requiredArray(terms.outputs, "Requested outputs").map(adaptOutputTerm);
    const materials = requiredArray(terms.materials, "Material terms").map(adaptMaterialTerm);
    if (new Set(outputs.map(item => item.lineId)).size !== outputs.length ||
        new Set(materials.map(item => item.lineId)).size !== materials.length) {
        throw new CommissionClientError(
            "The accepted terms contain duplicate line identities.",
            "invalid-projection");
    }
    return {
        version: requiredInteger(terms.version, "Terms version", 1),
        outputs,
        materials,
        payment: adaptPayment(requiredObject(terms.payment, "Payment terms")),
        deliveryInstructions: optionalText(terms.deliveryInstructions),
        pricingEvidence: adaptEvidence(requiredObject(terms.pricingEvidence, "Pricing evidence")),
        contactInstructions: optionalText(terms.contactInstructions)
    };
}

function adaptOutputTerm(value) {
    return {
        lineId: requiredGuid(value.lineId, "Output line ID"),
        itemId: requiredInteger(value.itemId, "Output item ID", 1),
        name: requiredText(value.name, "Output name"),
        requiredQuantity: requiredInteger(value.requiredQuantity, "Output quantity", 1),
        mustBeHq: requiredBoolean(value.mustBeHq, "Output quality")
    };
}

function adaptMaterialTerm(value) {
    return {
        lineId: requiredGuid(value.lineId, "Material line ID"),
        itemId: requiredInteger(value.itemId, "Material item ID", 1),
        name: requiredText(value.name, "Material name"),
        quantity: requiredInteger(value.quantity, "Material quantity", 1),
        requiresHq: requiredBoolean(value.requiresHq, "Material quality"),
        responsibility: requiredEnum(
            value.responsibility,
            enumMaps.materialResponsibility,
            "Material responsibility"),
        unitCost: requiredNumber(value.unitCost, "Material unit cost", 0),
        totalCost: requiredNumber(value.totalCost, "Material total cost", 0)
    };
}

function adaptPayment(value) {
    return {
        schedule: requiredEnum(value.schedule, enumMaps.paymentSchedule, "Payment schedule"),
        contractLabel: requiredText(value.contractLabel, "Payment contract"),
        materialReimbursement: requiredNumber(
            value.materialReimbursement,
            "Material reimbursement",
            0),
        materialAdjustment: requiredNumber(value.materialAdjustment, "Material adjustment", 0),
        craftLabor: requiredNumber(value.craftLabor, "Craft labor", 0),
        total: requiredNumber(value.total, "Total payment", 0),
        customTerms: optionalText(value.customTerms),
        craftSynthCount: requiredInteger(
            value.craftSynthCount ?? 0,
            "Craft synth count",
            0),
        gilPerSynth: requiredNumber(value.gilPerSynth ?? 0, "Gil per synth", 0)
    };
}

function adaptEvidence(value) {
    return {
        costBasis: requiredText(value.costBasis, "Pricing cost basis"),
        marketScope: requiredText(value.marketScope, "Pricing market scope"),
        location: requiredText(value.location, "Pricing location"),
        capturedAtUtc: requiredDate(value.capturedAtUtc, "Pricing capture time")
    };
}

function adaptPublicGates(value) {
    return {
        identity: requiredEnum(value.identity, enumMaps.clearance, "Identity clearance"),
        payment: requiredEnum(value.payment, enumMaps.clearance, "Payment clearance"),
        companyMaterials: requiredEnum(
            value.companyMaterials,
            enumMaps.clearance,
            "Company material clearance")
    };
}

function adaptPublicProgress(value) {
    const requiredQuantity = requiredInteger(value.requiredQuantity, "Required quantity", 1);
    const completedQuantity = requiredInteger(value.completedQuantity, "Completed quantity", 0);
    const readyQuantity = requiredInteger(value.readyQuantity, "Ready quantity", 0);
    const acceptedQuantity = requiredInteger(value.acceptedQuantity, "Accepted quantity", 0);
    if (completedQuantity > requiredQuantity ||
        readyQuantity > completedQuantity ||
        acceptedQuantity > readyQuantity) {
        throw new CommissionClientError(
            "The commission service returned impossible output quantities.",
            "invalid-output-quantities");
    }
    return {
        lineId: requiredGuid(value.lineId, "Progress line ID"),
        itemId: requiredInteger(value.itemId, "Progress item ID", 1),
        requiredQuantity,
        completedQuantity,
        readyQuantity,
        acceptedQuantity,
        updatedAtUtc: requiredDate(value.updatedAtUtc, "Progress update time")
    };
}

function adaptPublicReadiness(value) {
    return {
        isReady: requiredBoolean(value.isReady, "Delivery readiness"),
        declaredAtUtc: optionalDate(value.declaredAtUtc, "Readiness declaration time"),
        withdrawnAtUtc: optionalDate(value.withdrawnAtUtc, "Readiness withdrawal time")
    };
}

function adaptProvisionalCrafter(value) {
    return {
        provisionalCrafterId: requiredGuid(value.provisionalCrafterId, "Provisional crafter ID"),
        characterName: requiredText(value.characterName, "Character name"),
        homeWorld: requiredText(value.homeWorld, "Home world"),
        contactMethod: requiredText(value.contactMethod, "Contact method"),
        contactValue: requiredText(value.contactValue, "Contact value"),
        discordUserId: optionalText(value.discordUserId),
        discordDisplayNameSnapshot: optionalText(value.discordDisplayNameSnapshot),
        lodestoneCharacterId: requiredText(value.lodestoneCharacterId, "Lodestone character ID"),
        lodestoneProfileUrl: requiredHttpsUrl(value.lodestoneProfileUrl, "Lodestone profile URL"),
        submittedAtUtc: requiredDate(value.submittedAtUtc, "Identity submission time")
    };
}

function adaptParticipantActivity(value) {
    return {
        eventId: requiredGuid(value.eventId, "Activity event ID"),
        commissionRevision: requiredInteger(value.commissionRevision, "Activity revision", 1),
        actorKind: requiredEnum(value.actorKind, enumMaps.actorKind, "Activity actor kind"),
        actorDisplayName: optionalText(value.actorDisplayName),
        sourceSurface: requiredEnum(value.sourceSurface, enumMaps.sourceSurface, "Activity source"),
        createdAtUtc: requiredDate(value.createdAtUtc, "Activity time"),
        kind: requiredEnum(value.kind, enumMaps.activityKind, "Activity kind"),
        termsVersion: requiredInteger(value.termsVersion, "Activity terms version", 1),
        comment: optionalText(value.comment)
    };
}

function createSecret() {
    const bytes = crypto.getRandomValues(new Uint8Array(32));
    return btoa(String.fromCharCode(...bytes))
        .replaceAll("+", "-")
        .replaceAll("/", "_")
        .replaceAll("=", "");
}

function requiredObject(value, label) {
    if (!value || typeof value !== "object" || Array.isArray(value)) {
        throw new CommissionClientError(`${label} is missing from the projection.`, "invalid-projection");
    }
    return value;
}

function requiredArray(value, label) {
    if (!Array.isArray(value)) {
        throw new CommissionClientError(`${label} is missing from the projection.`, "invalid-projection");
    }
    return value;
}

function requiredText(value, label) {
    if (typeof value !== "string" || !value.trim()) {
        throw new CommissionClientError(`${label} is missing or invalid.`, "invalid-projection");
    }
    return value.trim();
}

function optionalText(value) {
    return typeof value === "string" && value.trim() ? value.trim() : null;
}

function optionalCapability(value, label) {
    const text = optionalText(value);
    if (text && !CAPABILITY_PATTERN.test(text)) {
        throw new CommissionClientError(`${label} is invalid.`, "invalid-authority");
    }
    return text;
}

function requiredGuid(value, label) {
    const text = requiredText(value, label);
    if (!GUID_PATTERN.test(text)) {
        throw new CommissionClientError(`${label} is invalid.`, "invalid-projection");
    }
    return text;
}

function requiredInteger(value, label, minimum) {
    if (!Number.isSafeInteger(value) || value < minimum) {
        throw new CommissionClientError(`${label} is missing or invalid.`, "invalid-projection");
    }
    return value;
}

function requiredNumber(value, label, minimum) {
    if (typeof value !== "number" || !Number.isFinite(value) || value < minimum) {
        throw new CommissionClientError(`${label} is missing or invalid.`, "invalid-projection");
    }
    return value;
}

function requiredBoolean(value, label) {
    if (typeof value !== "boolean") {
        throw new CommissionClientError(`${label} is missing or invalid.`, "invalid-projection");
    }
    return value;
}

function requiredDate(value, label) {
    const date = new Date(value);
    if (typeof value !== "string" || Number.isNaN(date.valueOf())) {
        throw new CommissionClientError(`${label} is missing or invalid.`, "invalid-projection");
    }
    return value;
}

function optionalDate(value, label) {
    return value == null ? null : requiredDate(value, label);
}

function requiredHttpsUrl(value, label) {
    const text = requiredText(value, label);
    try {
        const url = new URL(text);
        if (url.protocol !== "https:") {
            throw new Error();
        }
        return url.href;
    } catch {
        throw new CommissionClientError(`${label} is invalid.`, "invalid-projection");
    }
}

function optionalHttpsUrl(value) {
    return value == null || value === "" ? null : requiredHttpsUrl(value, "Optional URL");
}

function requiredEnum(value, values, label) {
    if (Number.isSafeInteger(value) && value >= 0 && value < values.length) {
        return values[value];
    }
    if (typeof value === "string" && values.includes(value)) {
        return value;
    }
    throw new CommissionClientError(`${label} is missing or invalid.`, "invalid-projection");
}

function statusCode(status) {
    if (status === 401 || status === 403) return "authority-unavailable";
    if (status === 404) return "commission-unavailable";
    if (status === 409) return "commission-conflict";
    if (status === 422) return "command-rejected";
    return "service-error";
}

function statusMessage(status) {
    if (status === 401 || status === 403) return "This browser does not hold valid authority for that action.";
    if (status === 404) return "This commission is unavailable.";
    if (status === 409) return "The commission changed before this action completed. Reload the current brief and try again.";
    if (status === 422) return "The commission cannot accept that action in its current state.";
    return "The commission service could not complete the request.";
}
