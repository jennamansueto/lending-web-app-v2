import { Component } from '@angular/core';

@Component({
  selector: 'app-home',
  template: `<p class="home-hint">Select a screen above to begin.</p>`,
  styles: [`.home-hint { color: dimgray; padding: 24px; }`],
})
export class Home {}
