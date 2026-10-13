import { Component } from '@angular/core';
import { RouterOutlet } from '@angular/router';

// The root of the app: nothing but the place where the router shows the current screen
// (app.routes.ts). Each screen brings its own frame.
@Component({
  imports: [RouterOutlet],
  selector: 'app-root',
  template: '<router-outlet />',
})
export class App {}
