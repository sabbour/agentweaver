import { API_URL } from '../config';
import { AgentweaverApiClient } from './client';
export const apiClient = new AgentweaverApiClient(API_URL);
