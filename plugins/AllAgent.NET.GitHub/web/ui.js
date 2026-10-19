import { registerElements } from '/__sdk/allagent-plugin-ui.mjs';

registerElements();
// Install before app.js attaches handlers; dynamic actions use the same custom element directly.
for (const original of document.querySelectorAll('button[data-iconiq]')) {
  const control = document.createElement('ag-button');
  for (const attribute of original.attributes) if (attribute.name !== 'data-iconiq') control.setAttribute(attribute.name, attribute.value);
  control.textContent = original.textContent;
  original.replaceWith(control);
}
await import('./app.js');
