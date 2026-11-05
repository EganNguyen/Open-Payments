// Helpers the harness needs locally. Vendor them instead of importing from a URL at run time.
export function uuidv4() {
  return "xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx".replace(/[xy]/g, (c) => {
    const r = (Math.random() * 16) | 0;
    const v = c === "x" ? r : (r & 0x3) | 0x8;
    return v.toString(16);
  });
}

// 80% of traffic goes to the 20% hot merchants.
export function pickMerchant() {
  const index =
    Math.random() < 0.8
      ? Math.floor(Math.random() * 20)
      : 20 + Math.floor(Math.random() * 80);
  return "merchant-" + index;
}

export function remember(ring, entry, limit) {
  ring.push(entry);
  while (ring.length > limit) {
    ring.shift();
  }
}

export function pickRecent(ring) {
  if (ring.length === 0) {
    return null;
  }
  return ring[Math.floor(Math.random() * ring.length)];
}
