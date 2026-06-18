import { AgentweaverApiClient } from './client';
import { API_KEY, API_URL } from '../config';

export const apiClient = new AgentweaverApiClient(API_URL, API_KEY);
