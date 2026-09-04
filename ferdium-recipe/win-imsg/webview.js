"use strict";

module.exports = (Ferdium) => {
  // The page authenticates via its session cookie (set on first load from the
  // ?token= service URL), so same-origin fetches here are already authorized.
  const getMessages = async () => {
    try {
      const response = await fetch("/api/badge");
      if (!response.ok) {
        return;
      }
      const badge = await response.json();
      Ferdium.setBadge(badge.unread ?? 0);
    } catch {
      // The companion app is not running; leave the badge unchanged.
    }
  };

  Ferdium.loop(getMessages);
};
