import { API_URL } from '../config';
import { AgentweaverApiClient } from './client';

/** Shared API client for browser calls to the Agentweaver REST service. */
export const apiClient = new AgentweaverApiClient(API_URL);
