(() => {
    "use strict";

    const byId = id => document.getElementById(id);
    const formatNumber = value => Number(value || 0).toLocaleString();
    const formatGil = value => `${Math.round(Number(value) || 0).toLocaleString()}g`;
    const formatPercent = value => `${Number(value || 0).toLocaleString(undefined, {
        maximumFractionDigits: 2
    })}%`;
    const formatDate = value => new Date(value).toLocaleString(undefined, {
        year: "numeric",
        month: "short",
        day: "numeric",
        hour: "numeric",
        minute: "2-digit"
    });
    const setText = (id, value) => {
        byId(id).textContent = value ?? "";
    };
    const showToast = message => {
        const toast = byId("toast");
        toast.textContent = message;
        toast.classList.add("is-visible");
        window.setTimeout(() => toast.classList.remove("is-visible"), 1600);
    };
    const copy = async (value, message) => {
        await navigator.clipboard.writeText(value);
        showToast(message);
    };

    function renderOutputs(outputs, deliveryInstructions) {
        const target = byId("outputs");
        target.replaceChildren();
        for (const output of outputs) {
            const row = document.createElement("div");
            row.className = "brief-output";
            const identity = document.createElement("div");
            const name = document.createElement("strong");
            name.textContent = output.name;
            const note = document.createElement("small");
            note.textContent = deliveryInstructions;
            identity.append(name, note);
            const quantity = document.createElement("span");
            quantity.className = "quantity";
            quantity.textContent = `×${formatNumber(output.quantity)}`;
            const quality = document.createElement("span");
            quality.className = "quality";
            quality.textContent = output.mustBeHq ? "HQ REQUIRED" : "NQ ACCEPTED";
            row.append(identity, quantity, quality);
            target.append(row);
        }
    }

    function renderMaterials(targetId, materials, companyProvides) {
        const target = byId(targetId);
        target.replaceChildren();
        if (!materials?.length) {
            const empty = document.createElement("p");
            empty.className = "material-empty";
            empty.textContent = companyProvides
                ? "The company is not providing materials for this commission."
                : "The crafter is not responsible for procuring materials.";
            target.append(empty);
            return;
        }

        for (const material of materials) {
            const row = document.createElement("div");
            row.className = "material-row";
            const identity = document.createElement("div");
            identity.className = "material-identity";
            const name = document.createElement("strong");
            name.textContent = material.name;
            const basis = document.createElement("small");
            const quality = material.requiresHq ? "HQ required" : "NQ accepted";
            basis.textContent = Number(material.unitCost) > 0
                ? `${quality} · ${formatGil(material.unitCost)} each`
                : quality;
            identity.append(name, basis);

            const quantity = document.createElement("span");
            quantity.className = "material-quantity";
            quantity.textContent = `×${formatNumber(material.quantity)}`;

            const total = document.createElement("strong");
            total.className = "material-total";
            if (Number(material.totalCost) > 0) {
                total.textContent = companyProvides
                    ? `${formatGil(material.totalCost)} value`
                    : formatGil(material.totalCost);
            } else {
                total.textContent = companyProvides ? "Provided" : "Included";
            }

            row.append(identity, quantity, total);
            target.append(row);
        }
    }

    function paymentExplanation(payment) {
        const percent = formatPercent(payment.materialAdjustmentPercent);
        if (payment.contractLabel.toLowerCase().includes("legacy")) {
            return Number(payment.materialAdjustmentPercent) > 0
                ? `This contract reimburses crafter-procured materials at the frozen item costs above, then adds ${percent} of that reimbursement as commission. Company-provided materials are excluded from both amounts.`
                : "This contract reimburses crafter-procured materials at the frozen item costs above, then adds the published material commission. Company-provided materials are excluded from both amounts.";
        }

        const laborBasis = Number(payment.craftSynthCount) > 0 && Number(payment.gilPerSynth) > 0
            ? ` Craft labor is ${formatNumber(payment.craftSynthCount)} synths at ${formatGil(payment.gilPerSynth)} per synth.`
            : "";
        return `This contract reimburses crafter-procured materials and adds a ${percent} material-value bonus. Company-provided materials are excluded.${laborBasis}`;
    }

    function paymentRows(payment) {
        const materialDetail = "Crafter-procured materials at the frozen item costs above";
        const adjustmentLabel = payment.contractLabel.toLowerCase().includes("legacy")
            ? "Legacy material commission"
            : "Material value bonus";
        const adjustmentDetail = Number(payment.materialAdjustmentPercent) > 0
            ? `${formatPercent(payment.materialAdjustmentPercent)} × ${formatGil(payment.materialReimbursement)} reimbursement`
            : "Published contract adjustment";
        const rows = [
            {
                label: "Crafter material reimbursement",
                detail: materialDetail,
                value: payment.materialReimbursement
            },
            {
                label: adjustmentLabel,
                detail: adjustmentDetail,
                value: payment.materialBonus
            }
        ];
        if (Number(payment.craftLabor) > 0) {
            const detail = Number(payment.craftSynthCount) > 0 && Number(payment.gilPerSynth) > 0
                ? `${formatNumber(payment.craftSynthCount)} synths × ${formatGil(payment.gilPerSynth)}`
                : "Published craft-labor amount";
            rows.push({ label: "Craft labor", detail, value: payment.craftLabor });
        }
        rows.push({ label: "Total payment", detail: "Reimbursement + contract adjustment + craft labor", value: payment.total, total: true });
        return rows;
    }

    function renderPayment(payment) {
        const target = byId("paymentBreakdown");
        target.replaceChildren();
        for (const entry of paymentRows(payment)) {
            const row = document.createElement("div");
            row.className = `money-row${entry.total ? " total" : ""}`;
            const label = document.createElement("div");
            label.className = "money-label";
            const name = document.createElement(entry.total ? "strong" : "span");
            name.textContent = entry.label;
            const detail = document.createElement("small");
            detail.textContent = entry.detail;
            label.append(name, detail);
            const amount = document.createElement("strong");
            amount.textContent = formatGil(entry.value);
            row.append(label, amount);
            target.append(row);
        }
        setText("paymentExplanation", paymentExplanation(payment));
    }

    function formatMaterialForCopy(material, companyProvides) {
        const quality = material.requiresHq ? " HQ" : "";
        const pricing = Number(material.unitCost) > 0
            ? ` — ${formatGil(material.unitCost)} each = ${formatGil(material.totalCost)}`
            : "";
        const responsibility = companyProvides ? " (company-provided; excluded from crafter payment)" : "";
        return `- ${material.name} ×${formatNumber(material.quantity)}${quality}${pricing}${responsibility}`;
    }

    function paymentCopyLines(payment) {
        return paymentRows(payment).map(row =>
            `${row.label}: ${formatGil(row.value)} — ${row.detail}`);
    }

    function buildCopyText(published) {
        const brief = published.brief;
        const outputs = brief.outputs
            .map(output => `- ${output.name} ×${formatNumber(output.quantity)}${output.mustBeHq ? " HQ" : " NQ accepted"}`);
        const crafterMaterials = brief.crafterMaterials?.length
            ? brief.crafterMaterials.map(material => formatMaterialForCopy(material, false))
            : ["- None"];
        const companyMaterials = brief.companyMaterials?.length
            ? brief.companyMaterials.map(material => formatMaterialForCopy(material, true))
            : ["- None"];
        const lines = [
            brief.title,
            `${brief.statusLabel} · ${brief.assignmentLabel}`,
            "",
            "Requested delivery",
            ...outputs,
            `Delivery instructions: ${brief.deliveryInstructions}`,
            "",
            "Crafter procures",
            ...crafterMaterials,
            "",
            "Company provides",
            ...companyMaterials,
            "",
            `Payment basis: ${brief.payment.contractLabel}`,
            ...paymentCopyLines(brief.payment),
            paymentExplanation(brief.payment),
            "",
            `Pricing evidence: ${brief.evidence.costBasis}; ${brief.evidence.marketScope}; ${brief.evidence.location}; captured ${formatDate(brief.evidence.capturedAtUtc)}`,
            `Reference: ${brief.reference}`
        ];
        if (brief.contact?.trim()) {
            lines.push(`Contact: ${brief.contact.trim()}`);
        }
        lines.push(`Brief: ${window.location.href}`);
        return lines.join("\n");
    }

    function render(published) {
        const brief = published.brief;
        const hasContact = Boolean(brief.contact?.trim());
        document.title = `${brief.title} — Commission Brief`;
        setText("companyName", brief.companyName);
        setText("briefTitle", brief.title);
        setText("publishedDate", `Private commission brief · Published ${formatDate(published.publishedAtUtc)}`);
        setText("statusChip", brief.statusLabel);
        setText("assignmentStatus", brief.assignmentLabel);
        setText("totalPayment", formatGil(brief.payment.total));
        setText("paymentContract", brief.payment.contractLabel);
        setText("termStatus", brief.statusLabel);
        setText("termAssignment", brief.assignmentLabel);
        setText("termPayment", brief.payment.contractLabel);
        setText("termVersion", published.version);
        setText("termReference", brief.reference);
        setText("evidenceTime", `Captured ${formatDate(brief.evidence.capturedAtUtc)}`);
        setText("evidenceLocation", brief.evidence.location);
        setText("evidenceScope", brief.evidence.marketScope);
        setText("evidenceBasis", brief.evidence.costBasis);
        setText("contactCompany", brief.companyName);
        setText("contactMethod", hasContact ? brief.contact.trim() : "");
        setText("contactReference", hasContact ? `Mention reference ${brief.reference}.` : "");
        renderOutputs(brief.outputs, brief.deliveryInstructions);
        renderMaterials("crafterMaterials", brief.crafterMaterials, false);
        renderMaterials("companyMaterials", brief.companyMaterials, true);
        renderPayment(brief.payment);

        byId("copyCommission").addEventListener("click", () =>
            copy(buildCopyText(published), "Full commission brief copied"));
        byId("copyContactAction").hidden = !hasContact;
        byId("contactSection").hidden = !hasContact;
        if (hasContact) {
            byId("copyContact").addEventListener("click", () =>
                copy(`${brief.contact.trim()}\nReference: ${brief.reference}`, "Contact details copied"));
        }
        byId("briefMessage").hidden = true;
        byId("briefShell").hidden = false;
    }

    async function load() {
        const publicId = new URLSearchParams(window.location.search).get("id");
        if (!publicId) {
            throw new Error("This commission link is incomplete.");
        }

        const response = await fetch(`/api/xivdata/commission-briefs/${encodeURIComponent(publicId)}`, {
            headers: { "Accept": "application/json" }
        });
        if (response.status === 404) {
            throw new Error("This commission link is unavailable or has been revoked.");
        }
        if (!response.ok) {
            throw new Error("The commission brief could not be loaded right now.");
        }
        render(await response.json());
    }

    load().catch(error => {
        setText("statusChip", "Unavailable");
        setText("messageTitle", "Commission brief unavailable");
        setText("messageBody", error.message);
    });
})();
