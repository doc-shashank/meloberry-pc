const metaEl = document.querySelector('meta[name="mby-token"]');
const token = metaEl?.getAttribute("content") || "";
if (metaEl) {
  metaEl.remove();
}

export async function api(method, url, body) {
  const opts = {
    method,
    headers: {
      "X-Mby-Token": token,
      "Content-Type": "application/json"
    }
  };
  if (body !== undefined) {
    opts.body = JSON.stringify(body);
  }
  const resp = await fetch(url, opts);
  const data = await resp.json().catch(() => ({}));
  return { ok: resp.ok, status: resp.status, data };
}

export function openEventStream() {
  return new EventSource(`/api/events?token=${encodeURIComponent(token)}`);
}
