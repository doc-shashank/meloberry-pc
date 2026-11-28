import { state } from "../../store.js";

export function findSpecMeta(topic) {
  const t = String(topic || "").toLowerCase();
  return state.specs.find(s => (s.topic || "").toLowerCase() === t) || null;
}

export function isImageCapableTopic(topic) {
  return Boolean(findSpecMeta(topic)?.supportsImage);
}

export function getSpecTemplateInfo(topic) {
  const spec = findSpecMeta(topic);
  return {
    hint: spec?.markdownHint || "Write content in Markdown or mby :::directive format.",
    starter: spec?.markdownStarter || ""
  };
}

export function getSpecModeStructure(specOrTopic) {
  const spec = typeof specOrTopic === "string" ? findSpecMeta(specOrTopic) : specOrTopic;
  const groups = spec?.modeGroups || [];
  return {
    spec,
    primaryGroup: groups[0] || null,
    secondaryGroups: groups.slice(1)
  };
}

export function findOrInitSelection(comp, topic) {
  let sel = comp.selections.find(s => s.topic.toLowerCase() === topic.toLowerCase());
  if (!sel) {
    sel = { topic, enabled: false, desiredCount: null, notes: "", modes: [], modeCounts: null };
    comp.selections.push(sel);
  }
  if (!Array.isArray(sel.modes)) sel.modes = [];
  return sel;
}

export function getActiveModeCounts(sel, primaryGroup) {
  if (!primaryGroup || !sel.modeCounts || typeof sel.modeCounts !== "object") return {};
  const valid = new Set(primaryGroup.options.map(o => o.key));
  const clean = {};
  for (const opt of primaryGroup.options) {
    const v = parseInt(sel.modeCounts[opt.key], 10);
    if (valid.has(opt.key) && Number.isFinite(v) && v > 0) clean[opt.key] = v;
  }
  return clean;
}

export function getPrimarySubtypeKeys(sel, primaryGroup) {
  if (!primaryGroup) return [];
  const counts = getActiveModeCounts(sel, primaryGroup);
  const countKeys = Object.keys(counts);
  if (countKeys.length > 0) return countKeys;
  const valid = new Set(primaryGroup.options.map(o => o.key));
  return (sel.modes || []).filter(m => valid.has(m));
}

export function syncModesFromDistribution(sel, primaryGroup, secondaryGroups) {
  const cleanCounts = getActiveModeCounts(sel, primaryGroup);
  const secondaryValid = new Set((secondaryGroups || []).flatMap(g => g.options.map(o => o.key)));
  const activeSecondary = (sel.modes || []).filter(m => secondaryValid.has(m));

  if (Object.keys(cleanCounts).length > 0) {
    sel.modeCounts = cleanCounts;
    const total = Object.values(cleanCounts).reduce((a, b) => a + b, 0);
    sel.desiredCount = total > 0 ? total : null;
    sel.modes = [...Object.keys(cleanCounts), ...activeSecondary];
  } else {
    sel.modeCounts = null;
    const primaryValid = new Set((primaryGroup?.options || []).map(o => o.key));
    const keepPrimary = (sel.modes || []).filter(m => primaryValid.has(m));
    sel.modes = [...keepPrimary, ...activeSecondary];
  }
}

export function handleSecondaryChipToggle(spec, sel, optKey, group) {
  sel.modes = sel.modes || [];
  if (group.multiSelect) {
    const idx = sel.modes.indexOf(optKey);
    if (idx >= 0) sel.modes.splice(idx, 1);
    else {
      sel.modes.push(optKey);
      if (spec.topic.toLowerCase() === "assertion-reason" && optKey === "both-false") {
        sel.modes = sel.modes.filter(m => m !== "4opt");
        if (!sel.modes.includes("5opt")) sel.modes.push("5opt");
        if (sel.modeCounts && sel.modeCounts["4opt"]) {
          sel.modeCounts["5opt"] = (sel.modeCounts["5opt"] || 0) + sel.modeCounts["4opt"];
          delete sel.modeCounts["4opt"];
        }
      }
    }
  } else {
    const siblingKeys = group.options.map(o => o.key);
    const wasActive = sel.modes.includes(optKey);
    sel.modes = sel.modes.filter(m => !siblingKeys.includes(m));
    if (!wasActive) sel.modes.push(optKey);
  }
}

export function buildOrganizationDisplayItems(comp) {
  if (comp.orderedItems && comp.orderedItems.length > 0) {
    const counters = {};
    return comp.orderedItems.map(inst => {
      const key = (inst.topic || "").toLowerCase();
      counters[key] = (counters[key] || 0) + 1;
      inst.instanceNumber = counters[key];
      return { ...inst, subtype: inst.subtype || null, isAuto: false };
    });
  }

  const preview = [];
  (comp.selections || []).filter(s => s.enabled).forEach(sel => {
    const { primaryGroup } = getSpecModeStructure(sel.topic);
    const isAuto = !sel.desiredCount || sel.desiredCount <= 0;
    if (isAuto) {
      const activePrimary = getPrimarySubtypeKeys(sel, primaryGroup);
      const sub = activePrimary.length === 1 ? activePrimary[0] : null;
      preview.push({
        id: `${comp.id}_${sel.topic}_1_auto`,
        topic: sel.topic,
        instanceNumber: 1,
        subtype: sub,
        isAuto: true
      });
      return;
    }

    const counts = getActiveModeCounts(sel, primaryGroup);
    const countKeys = Object.keys(counts);
    let num = 0;
    if (countKeys.length > 0) {
      countKeys.forEach(sub => {
        for (let i = 0; i < counts[sub] && num < sel.desiredCount; i++) {
          num++;
          preview.push({ id: `${comp.id}_${sel.topic}_${num}`, topic: sel.topic, instanceNumber: num, subtype: sub, isAuto: false });
        }
      });
    }
    const activePrimary = !countKeys.length ? getPrimarySubtypeKeys(sel, primaryGroup) : [];
    let fallbackIdx = 0;
    while (num < sel.desiredCount) {
      num++;
      const sub = activePrimary.length > 0
        ? activePrimary[fallbackIdx++ % activePrimary.length]
        : null;
      preview.push({ id: `${comp.id}_${sel.topic}_${num}`, topic: sel.topic, instanceNumber: num, subtype: sub, isAuto: false });
    }
  });
  return preview;
}

export function materializeOrderedItems(comp) {
  if (comp.orderedItems && comp.orderedItems.length > 0) return;
  comp.orderedItems = buildOrganizationDisplayItems(comp).map(it => ({
    id: it.id || `${comp.id}_${it.topic}_${it.instanceNumber}_${Math.random().toString(36).slice(2, 6)}`,
    topic: it.topic,
    instanceNumber: it.instanceNumber,
    subtype: it.subtype || null
  }));
}

export function syncSelectionsFromOrderedItems(comp, touchedTopic = null) {
  const counts = {};
  const topicOrder = [];
  const subtypeCountsByTopic = {};

  (comp.orderedItems || []).forEach(inst => {
    const k = (inst.topic || "").toLowerCase();
    if (!counts[k]) topicOrder.push(inst.topic);
    counts[k] = (counts[k] || 0) + 1;
    inst.instanceNumber = counts[k];
    if (inst.subtype) {
      subtypeCountsByTopic[k] = subtypeCountsByTopic[k] || {};
      subtypeCountsByTopic[k][inst.subtype] = (subtypeCountsByTopic[k][inst.subtype] || 0) + 1;
    }
  });

  topicOrder.forEach(topic => findOrInitSelection(comp, topic));

  const touchedKey = touchedTopic ? touchedTopic.toLowerCase() : null;
  comp.selections.forEach(sel => {
    const k = sel.topic.toLowerCase();
    const c = counts[k] || 0;
    if (c > 0) {
      sel.enabled = true;
      if (!(sel.desiredCount === null && c === 1 && touchedKey && k !== touchedKey)) {
        sel.desiredCount = c;
      }
      const subCounts = subtypeCountsByTopic[k];
      const { primaryGroup, secondaryGroups } = getSpecModeStructure(sel.topic);
      if (subCounts && Object.keys(subCounts).length > 0) {
        sel.modeCounts = subCounts;
        syncModesFromDistribution(sel, primaryGroup, secondaryGroups);
      } else {
        sel.modeCounts = null;
        const primaryValid = new Set((primaryGroup?.options || []).map(o => o.key));
        sel.modes = (sel.modes || []).filter(m => !primaryValid.has(m));
      }
    } else {
      sel.enabled = false;
      sel.desiredCount = null;
      sel.modeCounts = null;
    }
  });
}
