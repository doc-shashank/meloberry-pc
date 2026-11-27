export const MARKDOWN_SPEC_TEMPLATES = {
  text: {
    hint: "Standard Markdown + KaTeX ($inline$ or $$block$$).",
    starter: "## Heading\n\nExplain the core concept clearly here."
  },
  image: {
    hint: "Pick a workspace image or extract from PDF below. Optional caption & alt text.",
    starter: ""
  },
  callout: {
    hint: "Plain Markdown body (wrapped automatically with your selected tone: pearl / clinical / warning).",
    starter: "**Key takeaway:** Highlight the clinical pearl or pitfall here."
  },
  table: {
    hint: "Standard Markdown pipe table. Use hideableCols / stickyFirstCol subtypes for study recall.",
    starter: "| Feature | Entity A | Entity B |\n| --- | --- | --- |\n| Mechanism | Value A | Value B |"
  },
  "diagram-explore": {
    hint: "Attach an image above, then list clickable landmarks (`- Label: Explanation`).",
    starter: "- Landmark 1: Describe this structure.\n- Landmark 2: Describe its function."
  },
  reveal: {
    hint: "Use `> Step Title` lines to separate progressive reveal cards.",
    starter: "> Step 1: Trigger\nInitial physiological stimulus.\n\n> Step 2: Response\nDownstream compensatory mechanism."
  },
  link: {
    hint: "Hover card body shown when referenced via `[text](mby://card/card-1)`.",
    starter: "Concise definition or clinical note shown on hover."
  },
  mcq: {
    hint: "Write the question stem, then options with `- [x]` (correct) and `- [ ]` (distractor). Optional `> Explanation`.",
    starter: "Which mechanism best explains this finding?\n- [x] Correct option\n- [ ] Distractor A\n- [ ] Distractor B\n- [ ] Distractor C\n\n> **Explanation:** Brief rationale."
  },
  flashcard: {
    hint: "Separate sides with `:::front` and `:::back`. Attached images are placed on the front automatically.",
    starter: ":::front\nPrompt or structure to recall\n:::back\nConcise high-yield answer"
  },
  "fill-blank": {
    hint: "Wrap target answers in `{{primary | alias}}`.",
    starter: "Depolarization is driven by rapid influx of {{sodium | Na+}} ions."
  },
  "assertion-reason": {
    hint: "Provide `A:` (Assertion) and `R:` (Reason) lines.",
    starter: "A: Assertion statement.\nR: Reason explaining the assertion."
  },
  "spot-error": {
    hint: "Wrap checkable spans in `[valid span]` and errors in `[~~wrong~~ -> correction]` (at least 2 spans).",
    starter: "The [left ventricle] pumps blood into the [~~pulmonary artery~~ -> aorta]."
  },
  diff: {
    hint: "Pipe table using `{{answer}}` (fill mode) or `~~wrong~~ -> right` (spot-wrong mode).",
    starter: "| Property | Type A | Type B |\n| --- | --- | --- |\n| Onset | {{Acute}} | {{Chronic}} |"
  },
  mechanism: {
    hint: "Numbered pathway steps (`1.`, `2.`). Use duplicate numbers for parallel steps.",
    starter: "1. Initial trigger\n2. Intermediate cascade\n3. Final physiological effect"
  },
  relabel: {
    hint: "Attach a diagram above, then provide prompt and label boxes (`rect: [x, y, w, h]` in 0..1 coords).",
    starter: "prompt: Identify the labeled structures.\nboxes:\n  - label: \"Structure A\"\n    rect: [0.15, 0.2, 0.25, 0.1]"
  },
  classify: {
    hint: "Group items under `[Bucket Name]` headings.",
    starter: "[Category A]\n- Item 1\n- Item 2\n[Category B]\n- Item 3"
  },
  match: {
    hint: "List matching pairs as `- Left :: Right`.",
    starter: "- Receptor A :: Mechanism 1\n- Receptor B :: Mechanism 2"
  },
  numeric: {
    hint: "Use `:::numeric{answer=42 tolerance=\"5%\" unit=\"mL\"}` followed by the question prompt.",
    starter: ":::numeric{answer=42 tolerance=\"5%\" unit=\"units\"}\nCalculate the expected value.\n:::"
  },
  case: {
    hint: "Write the clinical vignette stem, followed by nested `::::mcq` or `::::numeric` sub-questions.",
    starter: "A 55-year-old patient presents with...\n::::mcq\nWhat is the most likely diagnosis?\n- [x] Diagnosis A\n- [ ] Diagnosis B\n::::"
  }
};

export function getMarkdownTemplateForTopic(topic) {
  const key = String(topic || "text").trim().toLowerCase();
  return MARKDOWN_SPEC_TEMPLATES[key] || {
    hint: "Write content in Markdown or mby :::directive format.",
    starter: ""
  };
}

export function extractSpecPreviewSnippet(content) {
  if (!content || !content.mode || content.mode === "none") return "";
  if (content.mode === "markdownFile" && content.markdownFilePath?.trim()) {
    return content.markdownFilePath.trim().split(/[\\/]/).pop() || "";
  }
  if (content.mode === "imageFile" && content.imagePath?.trim() && !content.rawText?.trim()) {
    return content.caption?.trim() || content.imagePath.trim().split(/[\\/]/).pop() || "";
  }
  const raw = (content.rawText || "").replace(/\r\n/g, "\n");
  const lines = raw.split("\n").map(l => l.trim()).filter(l =>
    l && !l.startsWith(":::") && !/^imagePath\s*:/i.test(l) && !/^prompt\s*:/i.test(l)
  );
  if (!lines.length) return content.imagePath?.trim()?.split(/[\\/]/).pop() || "";
  const clean = lines[0].replace(/^[#>*\-\s]+/, "").replace(/\*\*/g, "").trim();
  return clean.length > 44 ? `${clean.slice(0, 43)}\u2026` : clean;
}
