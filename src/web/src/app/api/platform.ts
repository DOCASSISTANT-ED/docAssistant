import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

// Registering companies (docs/decisions.md #59, #70): the shapes of the API's platform
// endpoints, written by hand after the server's types
// (Modules/Tenants/PlatformCompanyEndpoints.cs), and the calls. Only the platform admin
// may use them. Components use this service; they do not call HttpClient themselves.

// Server: CompanyResponse. emailDomains is empty for a company without an email of its own.
// hasRegistrationCode is false for companies registered before #70 until a code is made.
export interface Company {
  id: string;
  name: string;
  emailDomains: string[];
  hasRegistrationCode: boolean;
  adminCount: number;
  createdAt: string;
}

// Server: CreateCompanyRequest. Leave emailDomains empty for "no email of its own".
export interface CreateCompanyRequest {
  name: string;
  emailDomains: string[];
}

// Server: CompanyCreatedResponse. The code is in this answer only; the server keeps its hash.
export interface CreatedCompany {
  company: Company;
  registrationCode: string;
}

// Server: RegistrationCodeResponse.
export interface RenewedCode {
  registrationCode: string;
}

// The limits the server validates (CreateCompanyRequest, TenantConfiguration).
export const companyLimits = {
  nameMaxLength: 200,
  maxEmailDomains: 10,
};

@Injectable({ providedIn: 'root' })
export class PlatformApi {
  private readonly http = inject(HttpClient);
  private readonly companiesUrl = `${environment.apiBaseUrl}/platform/companies`;

  // Every company, by name.
  companies(): Observable<Company[]> {
    return this.http.get<Company[]>(this.companiesUrl);
  }

  // Fails with 409 when a company with the same name is registered (case, spaces and the
  // Turkish I do not count), and with 400 for a domain the server does not take.
  create(request: CreateCompanyRequest): Observable<CreatedCompany> {
    return this.http.post<CreatedCompany>(this.companiesUrl, request);
  }

  // A new code; the old one stops working at once. Fails with 404 for an unknown company.
  renewCode(companyId: string): Observable<RenewedCode> {
    return this.http.post<RenewedCode>(`${this.companiesUrl}/${companyId}/registration-code`, {});
  }
}
