import assert from "node:assert/strict";
import test from "node:test";

import { resolveParticipantPreworkChoices } from
    "../../src/FFXIV Craft Architect.Web/wwwroot/commission-client.js";

const activity = (kind, commissionRevision, termsVersion) => ({
    kind,
    commissionRevision,
    termsVersion
});

function projection({
    termsVersion = 2,
    paymentPending = true,
    materialsPending = true,
    paymentReceivedAtTermsVersion = null,
    activity: events = []
} = {}) {
    return {
        kind: "participant",
        public: {
            terms: { version: termsVersion },
            gates: {
                payment: paymentPending ? "Pending" : "Satisfied",
                companyMaterials: materialsPending ? "Pending" : "Satisfied"
            }
        },
        payment: {
            crafterReceived: paymentReceivedAtTermsVersion == null
                ? null
                : { termsVersion: paymentReceivedAtTermsVersion }
        },
        activity: events
    };
}

test("payment and current-terms material receipt are parallel pre-work choices", () => {
    const result = resolveParticipantPreworkChoices(projection({
        activity: [activity("CompanyMaterialsReady", 12, 2)]
    }));

    assert.equal(result.paymentPending, true);
    assert.equal(result.materialsPending, true);
    assert.equal(result.materialsReady, true);
    assert.deepEqual(result.choices.slice(0, 2), [
        "confirm-advance-payment",
        "acknowledge-company-materials"
    ]);
});

test("a terms revision does not replay stale material readiness", () => {
    const result = resolveParticipantPreworkChoices(projection({
        termsVersion: 2,
        activity: [activity("CompanyMaterialsReady", 8, 1)]
    }));

    assert.equal(result.materialsReady, false);
    assert.ok(!result.choices.includes("acknowledge-company-materials"));
    assert.ok(result.choices.includes("confirm-advance-payment"));
});

test("a terms revision does not replay a stale payment receipt", () => {
    const result = resolveParticipantPreworkChoices(projection({
        termsVersion: 2,
        paymentReceivedAtTermsVersion: 1,
        materialsPending: false
    }));

    assert.ok(result.choices.includes("confirm-advance-payment"));
    assert.ok(!result.choices.includes("retract-advance-payment-confirmation"));
});
