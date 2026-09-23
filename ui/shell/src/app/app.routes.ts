import { Route } from '@angular/router';
import { loadRemote } from '@module-federation/enhanced/runtime';
import { Home } from './home';

/** Remotes are resolved at runtime from `module-federation.manifest.json`, not bundled here. */
export const appRoutes: Route[] = [
  {
    path: 'loan-application',
    loadChildren: () =>
      loadRemote<typeof import('loanApplication/Routes')>('loanApplication/Routes').then(
        (m) => m!.remoteRoutes
      ),
  },
  {
    path: 'pricing',
    loadChildren: () =>
      loadRemote<typeof import('pricing/Routes')>('pricing/Routes').then((m) => m!.remoteRoutes),
  },
  {
    path: 'borrower-lookup',
    loadChildren: () =>
      loadRemote<typeof import('borrowerLookup/Routes')>('borrowerLookup/Routes').then(
        (m) => m!.remoteRoutes
      ),
  },
  {
    path: 'statements',
    loadChildren: () =>
      loadRemote<typeof import('statements/Routes')>('statements/Routes').then(
        (m) => m!.remoteRoutes
      ),
  },
  {
    path: '',
    component: Home,
  },
];
