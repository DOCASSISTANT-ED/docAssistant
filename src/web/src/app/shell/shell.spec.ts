import { Component, computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { CurrentUser } from '../api/identity';
import { Session } from '../auth/session';
import { Shell } from './shell';

@Component({ template: '<p class="inner-screen">İçerik</p>' })
class Inner {}

@Component({ template: '' })
class Blank {}

const admin: CurrentUser = {
  userId: 'u-1',
  email: 'ayse@example.com',
  tenantId: 't-1',
  role: 'Admin',
};

// The frame around the signed-in screens: who is signed in, and signing out. The
// session is a stand-in whose user the test chooses.
describe('Shell', () => {
  let harness: RouterTestingHarness;
  let shell: HTMLElement;
  let router: Router;
  let logout: ReturnType<typeof vi.fn>;

  async function open(user: CurrentUser | null): Promise<void> {
    const current = signal(user);
    logout = vi.fn(async () => undefined);

    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: '', component: Shell, children: [{ path: '', component: Inner }] },
          { path: 'login', component: Blank },
        ]),
        {
          provide: Session,
          useValue: {
            user: current.asReadonly(),
            isAdmin: computed(() => current()?.role === 'Admin'),
            logout,
          },
        },
      ],
    });

    harness = await RouterTestingHarness.create('/');
    shell = harness.routeNativeElement!;
    router = TestBed.inject(Router);
  }

  async function settle(): Promise<void> {
    await new Promise<void>((resolve) => setTimeout(resolve));
    harness.detectChanges();
  }

  // The menu and the notice are drawn outside the shell's own element, on top of the page.
  const page = () => document.body.textContent ?? '';

  async function openAccountMenu(): Promise<void> {
    shell.querySelector<HTMLButtonElement>('button.account')!.click();
    await settle();
  }

  async function pressSignOut(): Promise<void> {
    const item = Array.from(
      document.querySelectorAll<HTMLButtonElement>('button[mat-menu-item]'),
    ).find((button) => button.textContent?.includes('Çıkış yap'))!;

    item.click();
    await settle();
  }

  it('shows the current screen inside the frame', async () => {
    await open(admin);

    expect(shell.querySelector('.inner-screen')?.textContent).toBe('İçerik');
    expect(shell.querySelector('.brand-name')?.textContent).toBe('DocAssistant');
  });

  it('shows who is signed in', async () => {
    await open(admin);

    expect(shell.querySelector('.account-email')?.textContent).toBe('ayse@example.com');
    expect(shell.querySelector('.avatar')?.textContent).toBe('A');
  });

  // Turkish has two letters where English has one "i": the capital of "i" is "İ".
  it('writes the initial the Turkish way', async () => {
    await open({ ...admin, email: 'ilker@example.com' });

    expect(shell.querySelector('.avatar')?.textContent).toBe('İ');
  });

  it.each([
    ['Admin', 'Yönetici'],
    ['Member', 'Üye'],
  ] as const)('calls a user with the %s role "%s" in the account menu', async (role, label) => {
    await open({ ...admin, role });

    await openAccountMenu();

    expect(document.querySelector('.role')?.textContent).toBe(label);
    expect(document.querySelector('.account-card-email')?.textContent).toBe('ayse@example.com');
  });

  it('has no account menu when nobody is signed in', async () => {
    await open(null);

    expect(shell.querySelector('button.account')).toBeNull();
  });

  describe('signing out', () => {
    it('signs out and shows the login screen', async () => {
      await open(admin);
      await openAccountMenu();

      await pressSignOut();

      expect(logout).toHaveBeenCalledOnce();
      expect(router.url).toBe('/login');
    });

    // Only the API can remove the session cookie. If it was not reached the user is
    // still signed in, and must not be shown the login screen as if they were not.
    it('keeps the user where they are and says so when signing out fails', async () => {
      await open(admin);
      logout.mockRejectedValueOnce(new Error('unreachable'));
      await openAccountMenu();

      await pressSignOut();

      expect(router.url).toBe('/');
      expect(page()).toContain('Çıkış yapılamadı. Bağlantınızı kontrol edip yeniden deneyin.');
    });

    it('says nothing when signing out works', async () => {
      await open(admin);
      await openAccountMenu();

      await pressSignOut();

      expect(page()).not.toContain('Çıkış yapılamadı');
    });
  });
});
