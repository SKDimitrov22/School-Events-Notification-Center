// Convert UTC datetime strings to browser local time
document.querySelectorAll('time.local-time').forEach(el => {
  const d = new Date(el.dateTime);
  if (!isNaN(d)) {
    el.textContent = d.toLocaleString(undefined, {
      dateStyle: 'medium',
      timeStyle: 'short'
    });
  }
});

// Friendly local date/time pickers backed by a hidden UTC (ISO) field.
// The visible <input type="datetime-local"> is a UX helper only; the hidden
// field is what gets submitted, so the stored value stays UTC ("...Z").
document.querySelectorAll('input.js-local-datetime').forEach(picker => {
  const hidden = document.getElementById(picker.dataset.target);
  if (!hidden) {
    return;
  }

  const initial = new Date(hidden.value);
  if (!isNaN(initial)) {
    const offsetMs = initial.getTimezoneOffset() * 60000;
    picker.value = new Date(initial.getTime() - offsetMs).toISOString().slice(0, 16);
  }

  const form = picker.closest('form');
  if (form) {
    form.addEventListener('submit', () => {
      const local = new Date(picker.value);
      if (!isNaN(local)) {
        hidden.value = local.toISOString();
      }
      // If the picker is empty/invalid, leave the hidden field's value untouched.
    });
  }
});

// Copy-link buttons
document.querySelectorAll('.js-copy-link').forEach(btn => {
  btn.addEventListener('click', async () => {

    const url = btn.dataset.url
      ? new URL(btn.dataset.url, window.location.origin).href
      : window.location.href;

    const originalText = btn.textContent;
    const flash = (text) => {
      btn.textContent = text;
      setTimeout(() => {
        btn.textContent = originalText;
      }, 1500);
    };

    try {
      await navigator.clipboard.writeText(url);
      flash('Copied!');
    } catch (error) {
      // Clipboard API needs a secure context (https/localhost) and permission.
      window.prompt('Copy this link:', url);
    }

  });
});
